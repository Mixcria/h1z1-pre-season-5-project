#!/usr/bin/env python3
r"""derive_weapon_lists.py - the derive-layer document for `WeaponDefinitions` lists 2-7 and for
`ReferenceData "ProjectileDefinitions"`.

WHY A SIXTH DOCUMENT RATHER THAN MORE OF weapons.json
-----------------------------------------------------
`derived/weapons.json` is an input of the `weapon-firegroups` generator, and its sha256 is stamped
into the head of `AugustWeaponFacts.g.cs`. Adding keys to it would move that hash and force a
regeneration of a checked-in file whose body does not change - noise for every other lane working
in this tree at the same time (docs/96 "byte-identical rule"). This document is additive instead:
nothing that exists reads it, and the two `.g.cs` files it feeds are new.

WHAT IS IN IT
-------------
`fireModes`   one row per (fire group, mode index) that `WeaponDefinitionsBlob` list 2 ships. The
              ids are Cranberry's own (D159: `fireGroupId * 2 + index`) because the August client
              ships no fire-mode table and no datasheet names a fire-mode id; every VALUE is the
              client's own `ClientItemDatasheetData` row for the lowest-numbered item that names
              the group.

`projectiles` one row per `PROJECTILE_ID` in the client's own `ProjectileToPenTypes.txt`, with the
              pen type it maps to. These are the ids `ProjectileDefinitionsBlob` keys on.

`layout`      the record lengths this lane recovered from the binary, so the C# constants and the
              document cannot drift apart silently: a test asserts they agree.

SOURCES (all CLIENT, docs/00 rule 2)
  out\data_aug\ClientItemDatasheetData.txt   REFIRE_TIME_MS / RELOAD_TIME_MS / CLIP_SIZE / FIRE_GROUP_ID
  out\data_aug\ProjectileToPenTypes.txt      PROJECTILE_ID -> PEN_TYPE_ID
  out\data_aug\PenTypes.txt                  PEN_TYPE_ID -> name

Derivation and the [P]/[I]/[U] marks for every offset: docs/99-weapon-lists.md.
"""
from __future__ import annotations

import argparse
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

DEFAULT_DATA_DIR = Path(r"C:\Aug2017\out\data_aug")
DEFAULT_OUT = DEFAULT_DATA_DIR / "derived" / "weapon-lists.json"

#: RULING (D-row, 2026-09-03 wave 14 addendum). `DEFAULT_ZOOM` / `FireMode.DefaultZoom` is the
#: list-2 field the first-person camera zooms by: `FUN_141488490` reads `rec+0x128`,
#: `FUN_1411c8740` interpolates the character's own `+0x55a4` toward it, and the client's debug
#: HUD prints that word as "Zoom" (`FUN_140f786f0`). The MECHANISM is [P]; the VALUE is not in any
#: August sheet - `ClientItemDatasheetData` stops at MIN_CONE_OF_FIRE, `FireModeDisplayStats` is
#: ten damage columns of zeros, and no client file names a zoom. The one number the client itself
#: supplies is the band: `FUN_140e40090` / `FUN_140e4a010` treat zoom > `_DAT_143275150` = 2.0 as
#: SCOPED (scopedMouseSensitivity) and zoom > 1.0 as ADS (ADSMouseSensitivity). 1.5 is the midpoint
#: of that iron-sights band (1.0, 2.0]; it is a lever, not a discovery -
#: CRANBERRY_WEAPON_ADS_ZOOM overrides it live and CRANBERRY_WEAPON_ADS_FOV=0 reverts to 1.0.
#:
#: ADDENDUM 2026-09-03 (evening) - RETUNED 1.5 -> 1.2 on the owner's ruling. Right-click ADS in
#: retail August KOTK is a THIRD-PERSON over-the-shoulder aim, a modest tighten, NOT a big zoom;
#: the owner reported the 1.5 above "felt like a zoom". 1.2 is a gentler tighten and still inside
#: the client's own (1.0, 2.0] iron-sights band, so the client keeps treating it as an iron sight
#: (ADSMouseSensitivity, not scopedMouseSensitivity). It is a feel number; the lever still tunes
#: it. See docs/107 §12.
ADS_ZOOM = 1.2

#: RULING (D-row, 2026-09-03 wave 15). `FORCE_FP_SCOPE` / `rec+0x2dc` is the ONE field that makes
#: the August client's right-click leave the third-person camera: `FUN_14158ad50:17` reads that
#: byte off the LIVE fire-mode record, `FUN_14158b700` - the Infantry/SecondaryFire handler, which
#: `InputProfile_Default.xml:518` binds to `Mouse_1` under the display name `UI.WeaponOptic` -
#: branches on it, and `FUN_14158ab50` then calls `FUN_140b7a770(mgr, 0x1e)` (first person) while
#: the button is held and `0x24` (third person) on release, persisting the choice through
#: `FUN_1413ac350`, which writes `[General] FirstPerson` into `UserOptions.ini`. Because the flag
#: is read off the LIVE mode it has to sit on the PRIMARY mode (index 0) - the mode that is live
#: when the button goes down - and because the branch is exclusive with the iron-sights fire-mode
#: switch, turning it on replaces `82 0c SwitchFireModeRequest` with the camera change.
#:
#: ADDENDUM 2026-09-03 (evening) - DEFAULT FLIPPED True -> False on the owner's ruling. He does
#: NOT want right-click to switch to first person: retail August KOTK right-click ADS is a
#: THIRD-PERSON over-the-shoulder aim, not first person and not the big zoom either. With this
#: False, FORCE_FP_SCOPE and FP_FORCE_CAMERA_OVERRIDES ship 0, so the client keeps its
#: third-person camera and takes the iron-sights fire-mode branch (82 0c) with the modest ADS_ZOOM
#: above and the IRON_SIGHTS_MS ramp below. The [P] mechanism is unchanged and CRANBERRY_WEAPON_ADS_FP=1
#: restores the first-person swap for anyone who wants it. This matches the owner's own Z1, which
#: ships force_fp_scope = false (ZoneWeaponDefinitions.cs:1305). See docs/107 §12, D-row D233.
ADS_FIRST_PERSON = False

#: RULING (D-row, 2026-09-03 wave 15). The aimed FIRST-PERSON field of view in DEGREES, written
#: into `FP_CAMERA_FOV` / `FP_CR_CAMERA_FOV` / `FP_PR_CAMERA_FOV` (`rec+0x2d0`, `+0x2d4`, `+0x2d8`)
#: behind the gate `FP_FORCE_CAMERA_OVERRIDES` (`rec+0x2cd`) that `FUN_140e3fff0` tests. It is an
#: ABSOLUTE FOV, so it is only defensible once ADS_FIRST_PERSON is on and first person IS the aim
#: state. The number is the client's own default vertical FOV - 65, the default
#: `FUN_1413a3e30:233-236` hands the `[Rendering] VerticalFOV` ini read, clamped there to a maximum
#: of 74 (`0x4a`) - divided by ADS_ZOOM, the (1.0, 2.0] band midpoint D212 already ruled. 0 clears
#: the gate and leaves the player's own verticalFOV standing.
ADS_FP_FOV = round(65.0 / ADS_ZOOM, 4)

#: ADOPTED from the owner's own Z1 under D53: C:\Z1\Server\Zone\ZoneWeaponDefinitions.cs lines
#: 1358-1361 write 266 ms into all four of `to_iron_sights_ms`, `from_iron_sights_ms`,
#: `to_iron_sights_anim_ms` and `from_iron_sights_anim_ms`. Cranberry writes the two the August
#: client's own aim path reads - `Weapon.ToIronSightsTime` (`def+0x38`) and
#: `Weapon.FromIronSightsTime` (`def+0x3c`), taken by `FUN_1422935d0:70-76` - so the fire-mode
#: switch RAMPS instead of snapping. No August sheet carries either column.
IRON_SIGHTS_MS = 266

#: D159. The August client ships no fire-mode table, so the ids are the server's to choose.
FIRE_MODES_PER_GROUP = 2

#: Record lengths recovered from the August binary this lane (docs/99). Mirrored by the C#
#: constants in WeaponListLayouts / FireModeRecord / ProjectileDefinitionRecord.
LAYOUT = {
    "fireModeBodyBytes": 631,          # FUN_140a422d0, 180 reads
    "fireModeRecordBytes": 639,        # + u32 id + u32 rec+0x18
    "fireModeFieldCount": 180,
    "fireModeUnitScalarCount": 34,     # FUN_1422267e0 presets of 0x3f800000
    "list3ElementBytes": 72,           # FUN_140a41f00 + its key
    "list4RecordBytes": 16,            # FUN_140a39ca0 + its key (FireModeProjectileMapping)
    "ammoSlotRowBytes": 37,            # FUN_140a2c690, three empty strings
    "list5RecordBytes": 96,            # FUN_140a2c1b0 + its key
    "list6ElementBytes": 16,           # FUN_140a3f970 + its key
    "list7ElementBytes": 4,            # FUN_140a4cbf0
    "projectileRecordMinimalBytes": 183,   # FUN_140a40410 (179) + its key
}


def read_sheet(path: Path) -> list[dict[str, str]]:
    """The client's own `^`-separated datasheet format: a `#*`-prefixed header, then rows."""
    text = path.read_text(encoding="utf-8-sig", errors="replace")
    lines = [ln for ln in text.splitlines() if ln.strip()]
    if not lines:
        return []
    header = lines[0].lstrip("#*").rstrip("^").split("^")
    rows = []
    for line in lines[1:]:
        cells = line.rstrip("^").split("^")
        if len(cells) < len(header):
            cells += [""] * (len(header) - len(cells))
        rows.append(dict(zip(header, cells)))
    return rows


def to_int(value: str) -> int:
    try:
        return int(float(value))
    except (TypeError, ValueError):
        return 0


def build(data_dir: Path) -> dict:
    datasheet = read_sheet(data_dir / "ClientItemDatasheetData.txt")
    pen_map = read_sheet(data_dir / "ProjectileToPenTypes.txt")
    pen_types = read_sheet(data_dir / "PenTypes.txt")
    effect_groups = read_sheet(data_dir / "FireModeEffectGroups.txt")

    pen_names = {to_int(r.get("ID", "")): (r.get("DESCRIPTION") or r.get("NAME") or "").strip()
                 for r in pen_types}

    # EFFECT_GROUP (rec+0x104): a FireModeEffectGroups GROUP_ID, NOT a composite-effect id. When a
    # weapon FIRES the client reads this group off the live fire mode, looks the (group, 'fire') pair
    # up in FireModeEffectGroups.txt itself, and queues the resulting composite effect - the muzzle
    # flash and the gunshot audio (HasAudioOnCompositeEffect). Cranberry shipped 0 from wave 9 to
    # wave 16 and the client logged "Failed to QueueCompositeEffectAtLocation due to missing effect
    # definition for Id #0!" - the owner's "no noise of being shot".
    #
    # Two facts fix what to ship: (1) rec+0x104 is a GROUP id (the client names a direct composite
    # column separately, MELEE_COMPOSITE_EFFECT_ID at rec+0x190), and it loads FireModeEffectGroups
    # at runtime as a (group, tag) -> composite lookup - the per-tag split (group 6 fire=105 vs
    # group 7 fire=106, and dryfire/state tags) proves one stored value cannot be the composite. So
    # ship the GROUP id, and the client resolves 6 -> 105 itself. (2) The FIRE_GROUP -> EFFECT_GROUP
    # mapping lives ONLY in the binary FireModes table (no plaintext sheet), so the best available
    # value is the fire-group id itself (the assumption weapons.json already makes), and it is only
    # defensible for a group that HAS a FireModeEffectGroups 'fire' row with a non-zero effect -
    # groups 1..21/23/24. A group with no row (every group > 24, e.g. the AK-47's group 51) is a
    # documented GAP and ships 0. RULING D234; one click on the client log settles group 6 (Id #105
    # means it worked; Id #0 means the fire-group != effect-group assumption is wrong for it).
    fire_effect_by_group: dict[int, int] = {}
    for row in effect_groups:
        # The sheet marks two key columns with a leading '*' (GROUP_ID and TAG); read_sheet only
        # strips the '#*' off the first header, so the second key survives as "*TAG".
        tag = (row.get("*TAG") or row.get("TAG") or "").strip()
        if tag == "fire":
            fire_effect_by_group[to_int(row.get("GROUP_ID", ""))] = to_int(row.get("EFFECT_ID", ""))

    # The lowest item id that names each fire group - the deterministic representative for a
    # per-group value taken out of a per-item sheet (docs/99 section 2.4).
    first_of_group: dict[int, dict[str, str]] = {}
    for row in datasheet:
        group = to_int(row.get("FIRE_GROUP_ID", ""))
        if group == 0:
            continue
        item = to_int(row.get("ITEM_ID", ""))
        current = first_of_group.get(group)
        if current is None or item < to_int(current.get("ITEM_ID", "")):
            first_of_group[group] = row

    fire_modes = []
    for group in sorted(first_of_group):
        row = first_of_group[group]
        clip = to_int(row.get("CLIP_SIZE", ""))
        refire = to_int(row.get("REFIRE_TIME_MS", ""))
        reload_ms = to_int(row.get("RELOAD_TIME_MS", ""))
        for index in range(FIRE_MODES_PER_GROUP):
            mode_id = (group * 2) + index
            fire_modes.append({
                "fireGroupId": group,
                "modeIndex": index,
                # D159: fireGroupId * 2 + index.
                "fireModeId": mode_id,
                "sourceItemId": to_int(row.get("ITEM_ID", "")),
                # rec+0x18 is the fire mode's OWN id: the stat-override scope key
                # (FUN_1422a5150 / FUN_1422a50a0), the primary key of list 4 (FUN_14228fcf0),
                # and the value FUN_142291b90 requires to be > 0. It mirrors rec+0x378 exactly
                # as list 0 mirrors its own id into def+0x18. WAVE 14 - it was "triggerCharge",
                # which gave 60 of the 120 modes the same id.
                "definitionId": mode_id,
                # rec+0x30 AMMO_SLOT [P]. Cranberry declares one slot per gun, so 0.
                "ammoSlot": 0,
                # rec+0x20 bit 0x04 IRON_SIGHTS [P]. RULING: mode index 1 is the ADS mode.
                "ironSights": index == 1,
                "clipSize": clip,            # the client's own CLIP_SIZE, for the ammo slot
                "refireTimeMs": refire,      # rec+0x40 FireMode.RefireTime  [P]
                "reloadTimeMs": reload_ms,   # rec+0x60 FireMode.ReloadTime  [P since wave 14]
                # rec+0x104 EFFECT_GROUP - the FireModeEffectGroups GROUP_ID the client resolves to a
                # fire composite (muzzle + gunshot audio). Ship the fire-group id itself, but only
                # for a group whose 'fire' row is non-zero; every other group (fire=0, or no row at
                # all - the AK-47's 51 included) ships 0. See the comment above (D234).
                "effectGroup": group if fire_effect_by_group.get(group, 0) != 0 else 0,
            })

    projectiles = []
    for row in pen_map:
        projectile_id = to_int(row.get("PROJECTILE_ID", ""))
        if projectile_id == 0:
            continue
        pen = to_int(row.get("PEN_TYPE_ID", ""))
        projectiles.append({
            "projectileId": projectile_id,
            "penTypeId": pen,
            "penTypeName": pen_names.get(pen, ""),
        })
    projectiles.sort(key=lambda p: p["projectileId"])

    return {
        "schema": "cranberry/weapon-lists",
        "schemaVersion": 1,
        "clientBuild": "0.0.118.208059",
        "generated": {
            "tool": "tools/data/derive_weapon_lists.py",
            "utc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "dataDir": data_dir.as_posix(),
        },
        "summary": (
            "WeaponDefinitions lists 2-7 and ProjectileDefinitions, for the August client "
            "(ClientProtocol_1148). Layouts are DERIVED from H1Z1.exe (docs/99); ids and times "
            "are the client's own datasheets; the fire-mode ids are Cranberry's (D159)."
        ),
        "counts": {
            "fireGroups": len(first_of_group),
            "fireModes": len(fire_modes),
            "projectiles": len(projectiles),
        },
        "sources": {
            "ClientItemDatasheetData.txt": len(datasheet),
            "ProjectileToPenTypes.txt": len(pen_map),
            "PenTypes.txt": len(pen_types),
            "FireModeEffectGroups.txt": len(effect_groups),
        },
        "layout": LAYOUT,
        "fireModesPerGroup": FIRE_MODES_PER_GROUP,
        "adsZoom": ADS_ZOOM,
        "adsFirstPerson": ADS_FIRST_PERSON,
        "adsFpFov": ADS_FP_FOV,
        "ironSightsMs": IRON_SIGHTS_MS,
        "fireModes": fire_modes,
        "projectiles": projectiles,
        "gaps": [
            "lists 3, 5, 6 and 7 are shipped EMPTY: the layouts are [P] but the roles are [I] "
            "(list 3 ConeOfFire, list 5 AimAssist) or [U] (lists 6, 7), and no client sheet "
            "supplies values keyed the way those records are. LIST 4 IS NO LONGER ONE OF THEM - "
            "wave 14 recovered its role (the fire-mode to projectile mapping) and Cranberry fills "
            "it from AmmoTypes x ProjectileToPenTypes, in hand-written C#, because the "
            "weapon-to-ammunition pairing comes out of the client's LOCALE text rather than out "
            "of a datasheet",
            "the ADS zoom MECHANISM is [P] as of the wave-14 addendum and the value is a RULING: "
            "FireMode.DefaultZoom is rec+0x128 (the FireModes datasheet loader stores the "
            "DEFAULT_ZOOM column at obj+0x108 and FUN_141488490 reads rec+0x128 under the key "
            "DAT_1452362c8, whose initialiser string is \"FireMode.DefaultZoom\"), but no August "
            "sheet carries a zoom column at all, so ADS_ZOOM above is Cranberry's number - the "
            "midpoint of the (1.0, 2.0] band the client itself uses to pick ADS over scoped "
            "mouse sensitivity",
            "the ADS FIRST-PERSON switch is [P] and the two numbers behind it are RULINGS: "
            "FORCE_FP_SCOPE (rec+0x2dc) is read off the LIVE fire mode by FUN_14158ad50 and "
            "makes FUN_14158b700 - the Mouse_1 Infantry/SecondaryFire handler - swap the "
            "camera (FUN_14158ab50 -> FUN_140b7a770 0x1e / 0x24) INSTEAD OF switching the "
            "fire mode, so it goes on the PRIMARY mode and it suppresses 82 0c while it is "
            "on; FP_FORCE_CAMERA_OVERRIDES / FP_CAMERA_FOV / FP_CR_ / FP_PR_ (rec+0x2cd, "
            "+0x2d0, +0x2d4, +0x2d8, read by FUN_140e3fff0) are then the ONLY field of view "
            "the aimed view has, because the unreachable iron-sights mode is where "
            "DEFAULT_ZOOM lives - they are ABSOLUTE degrees and ADS_FP_FOV above is "
            "Cranberry's number, the client's own default verticalFOV over ADS_ZOOM",
            "the FIRST_PERSON_OFFSET_X/Y/Z columns (rec+0x12c..+0x134) place the weapon in "
            "the first-person view and ship 0: no August sheet carries them and three "
            "invented floats per fire mode is exactly what D143 forbids",
            "FireMode recoil and bloom are not filled: the client carries no such sheet and the "
            "offsets that would take them are [U]",
        ],
    }


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--data-dir", type=Path, default=DEFAULT_DATA_DIR)
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT)
    ap.add_argument("--indent", type=int, default=1)
    args = ap.parse_args(argv)

    doc = build(args.data_dir)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(
        json.dumps(doc, indent=args.indent or None, ensure_ascii=False) + "\n",
        encoding="utf-8")
    print(f"{args.out}: {doc['counts']['fireModes']} fire modes, "
          f"{doc['counts']['projectiles']} projectiles")
    return 0


if __name__ == "__main__":
    sys.exit(main())
