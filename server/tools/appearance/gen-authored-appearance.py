#!/usr/bin/env python3
r"""gen-authored-appearance - Cranberry's OWN appearance rows for the thirteen grey loot wearables.

WHY THIS TOOL EXISTS (docs/106 addendum 2026-09-03)

  The boot census names thirteen ids on both bodies and says the same thing about every one of
  them: *"no appearance row at all, and the mesh is tintable - it composites white.dds /
  grey.dds, i.e. GREY"*. They are ground-loot wearables, so none of them is visible in the menu
  and all of them are visible in Z2, which is the second half of the owner's "the skins are grey
  in the real world" report; docs/106's three fixes were the other half.

      2112 2113 2115 2117  Mansport backpacks   SurvivorMale/Female_Back_Backpack_Mansport_Tintable.adr
      2127 2137 2139 2141
      2142 2145             TintTshirt shirts   SurvivorMale/Female_Chest_Shirt_TintTshirt.adr
      2168 2171             motorcycle helmets  SurvivorMale/Female_Head_Helmet_Motorcycle_Tintable.adr
      2218                  Conveys sneakers    SurvivorMale/Female_Feet_Conveys_Tintable.adr

  The rows this tool writes are **Cranberry's own**. The D22-gated capture does hold 26 rows for
  these thirteen items, but `AugustDynamicAppearanceTable.Filter` drops every row whose item is
  outside `AugustSkinCatalog`'s `wantedItems`, and none of the thirteen is a wardrobe reward - so
  they never reach the wire. Widening that filter would import somebody else's colourways;
  authoring them keeps the clean-room line (docs/00) and keeps the D22 MD5 gate untouched.

WHERE EVERY NUMBER COMES FROM

  * **the mesh** - `Models.txt` (the client's own actor catalogue) gives the `*ID`, the file name
    and the `GENDER` of each of the eight `.adr`s, so the row's `ModelId` and `GenderId` are the
    client's, not a guess;
  * **the colour map** - the mesh's `.adr` names its `_C.dds` texture alias, and
    `tools/appearance/ddstint.py` decodes that DXT1 map and measures its luminance percentiles.
    All four are bright greyscale luminance masks, which is *why* they render grey;
  * **the ramp shape** - highlight / midtone / shadow are the map's own p92 / p50 / p08 luminance
    ratios (docs/69 section 3.4's three-point model). Nothing about the shape is typed by hand;
  * **the hue** - the item's own name in the client's locale: 2112 is "Black Backpack", 2115 is
    "Green Backpack", 2171 is "White Motorcycle Helmet". The client ships the *name* of every
    colourway and not the colourway, so the hue is the one ruling in this file
    (`PALETTE` below, D203);
  * **the alpha and the neutral B branch** - `TintSemanticTables.txt` row 1 `Default`, read at
    generate time rather than transcribed.

INPUT
    C:\Aug2017\out\data_aug\Models.txt                     the client's actor catalogue
    C:\Aug2017\out\data_aug\TintSemanticTables.txt         row 1 `Default`: the neutral shape
    C:\Aug2017\out\data_aug\adr\<mesh>.adr                 the mesh's texture aliases
    C:\Aug2017\out\data_aug\derived\loot.json              ClientItemDefinitions.NAME_ID -> locale
    C:\Aug2017\out\appearance-tints\<map>.dds              the colour maps, measured here

OUTPUT
    src/Cranberry.Zone/Appearance/AugustAuthoredAppearance.g.cs

USAGE
    python tools/appearance/gen-authored-appearance.py --out src/Cranberry.Zone/Appearance
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ddstint import measure, ramp  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_DATA = Path(r"C:\Aug2017\out\data_aug")
DEFAULT_TINTS = Path(r"C:\Aug2017\out\appearance-tints")
OUT_DIR = ROOT / "src" / "Cranberry.Zone" / "Appearance"

MALE = 1
FEMALE = 2

#: The client's own actor catalogue, for the drift check every generator in this tree makes.
EXPECTED_MODEL_ROWS = 1173

#: Cranberry's own id space. "CRB" + a local ordinal keeps authored groups clear of the client's
#: low ids exactly as DynamicAppearanceReference's starter groups (0x43524201..03) already do,
#: and "CRR" does the same for row ids. Neither can ever collide with a captured value: the
#: capture's largest row id is four digits and its largest group id is four digits.
AUTHORED_GROUP_BASE = 0x4352_4210
AUTHORED_ROW_BASE = 0x4352_5200

#: The mesh family each item wears, and the item ids the boot census names for it
#: (`logs/host-20260902-212957.log:17-42`, verdict RendersGrey, "no appearance row at all").
#: This roster is the lane's ruling; everything else in the file is measured or read.
FAMILIES: list[tuple[str, str, str, tuple[int, ...]]] = [
    ("backpack",
     "SurvivorMale_Back_Backpack_Mansport_Tintable.adr",
     "SurvivorFemale_Back_Backpack_Mansport_Tintable.adr",
     (2112, 2113, 2115, 2117)),
    ("shirt",
     "SurvivorMale_Chest_Shirt_TintTshirt.adr",
     "SurvivorFemale_Chest_Shirt_TintTshirt.adr",
     (2127, 2137, 2139, 2141, 2142, 2145)),
    ("helmet",
     "SurvivorMale_Head_Helmet_Motorcycle_Tintable.adr",
     "SurvivorFemale_Head_Helmet_Motorcycle_Tintable.adr",
     (2168, 2171)),
    ("footwear",
     "SurvivorMale_Feet_Conveys_Tintable.adr",
     "SurvivorFemale_Feet_Conveys_Tintable.adr",
     (2218,)),
]

#: **D203, the one ruling in this file.** The August client ships a "Black Backpack" and a "Green
#: Backpack" on one mesh with one colour map; what separates them was a server-authored shader
#: parameter group, and the client keeps only the NAME. These are Cranberry's readings of those
#: names - mid-tone anchors in the client's 0..1 RGB, deliberately unsaturated because they are
#: multiplied onto an already-bright luminance mask.
PALETTE: dict[str, tuple[float, float, float]] = {
    "black": (0.055, 0.055, 0.060),
    "white": (0.900, 0.900, 0.905),
    "grey": (0.440, 0.440, 0.455),
    "blue": (0.145, 0.290, 0.640),
    "navy": (0.075, 0.110, 0.270),
    "green": (0.150, 0.400, 0.180),
    "olive": (0.290, 0.310, 0.170),
    "red": (0.610, 0.105, 0.100),
    "orange": (0.860, 0.415, 0.090),
}

#: Spellings the client's own item names use for the palette entries above.
SYNONYMS = {"gray": "grey", "grey": "grey"}

#: "Green Army" is one colour, not two. Checked before the single words.
COMPOUNDS: list[tuple[tuple[str, ...], str]] = [
    (("green", "army"), "olive"),
    (("army", "green"), "olive"),
]

#: A camouflage pattern is the same hue twice, light over dark. The factor is Cranberry's.
CAMO_SECONDARY_FACTOR = 0.55


def datasheet(path: Path) -> list[dict[str, str]]:
    lines = path.read_text(encoding="latin-1").splitlines()
    if not lines or not lines[0].startswith("#"):
        sys.exit(f"{path}: missing datasheet header")
    header = lines[0].lstrip("#").rstrip("^").split("^")
    return list(csv.DictReader(lines[1:], fieldnames=header, delimiter="^"))


def cs_string(value: str) -> str:
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


def f(value: float) -> str:
    return f"{value:.6f}f"


def colour_words(name: str) -> tuple[list[str], bool]:
    """The palette words in an item's own name, in order, plus whether it is a camo pattern."""
    tokens = [t for t in re.split(r"[^A-Za-z]+", name.lower()) if t]
    tokens = [SYNONYMS.get(t, t) for t in tokens]
    camo = "camo" in tokens or "camouflage" in tokens

    words: list[str] = []
    index = 0
    while index < len(tokens):
        pair = tuple(tokens[index:index + 2])
        for compound, colour in COMPOUNDS:
            if pair == compound:
                words.append(colour)
                index += 2
                break
        else:
            if tokens[index] in PALETTE:
                words.append(tokens[index])
            index += 1
    return words, camo


def texture_alias(adr: Path) -> str:
    """The mesh's colour map: the `_C.dds` texture alias of its `.adr` (docs/69 section 3.1)."""
    text = adr.read_text(encoding="latin-1")
    names = re.findall(r'textureName="([^"]+)"', text)
    colour = [n for n in names if n.lower().endswith("_c.dds")]
    if len(colour) != 1:
        sys.exit(f"{adr.name}: expected exactly one _C.dds texture alias, found {colour}")
    return colour[0]


def default_tint_row(rows: list[dict[str, str]]) -> dict[str, tuple[float, float, float, float]]:
    """`TintSemanticTables.txt` row 1 `Default`, by semantic name (docs/69 section 3.4)."""
    out: dict[str, tuple[float, float, float, float]] = {}
    for row in rows:
        if row["ALIAS_NAME"] != "Default" or row["EDIT_TYPE"] != "RGB":
            continue
        out[row["SEMANTIC_NAME"]] = (
            float(row["FLOAT_1"]), float(row["FLOAT_2"]),
            float(row["FLOAT_3"]), float(row["FLOAT_4"]))
    for needed in ("BaseTintHighlightA", "BaseTintMidtoneA", "BaseTintShadowA",
                   "BaseTintHighlightB", "BaseTintMidtoneB", "BaseTintShadowB"):
        if needed not in out:
            sys.exit(f"TintSemanticTables.txt has no Default row for {needed}")
    return out


HEADER = '''// <auto-generated>
// Generated by tools/appearance/gen-authored-appearance.py. Do not edit.
//
// CRANBERRY-AUTHORED DynamicAppearance rows for the {items} ground-loot wearables the boot
// census reports as GREY. Nothing here is copied from the D22-gated capture or from any other
// server's table: the mesh, its model id and its gender are the August client's own Models.txt;
// the ramp SHAPE is measured off the mesh's own colour map by tools/appearance/ddstint.py; the
// HUE is Cranberry's reading of the item's own name in the client's locale (D203). Client build
// 0.0.118.208059.
//
// measured colour maps:
{maps}// </auto-generated>

#nullable enable

using System.Collections.Immutable;
using System.Numerics;

namespace Cranberry.Zone.Appearance;

/// <summary>
/// One Cranberry-authored appearance row: the same five fields
/// <see cref="AugustAppearanceRow"/> carries, before it reaches the wire.
/// </summary>
/// <param name="RowId">Cranberry's own row id (0x43525200 + ordinal); it can collide with no captured id.</param>
/// <param name="ItemDefinitionId">The item this row dresses.</param>
/// <param name="ModelId">A <c>Models.txt</c> row - the client's own id for the worn mesh.</param>
/// <param name="GenderId">1 male, 2 female - the <c>GENDER</c> column of that same row.</param>
/// <param name="ShaderParameterGroupId">Cranberry's own colourway (0x43524210 + ordinal).</param>
public readonly record struct AugustAuthoredAppearanceRow(
    uint RowId,
    uint ItemDefinitionId,
    uint ModelId,
    uint GenderId,
    uint ShaderParameterGroupId);

/// <summary>
/// The authored rows and their shader parameters, added to the transmitted appearance table when
/// <c>CRANBERRY_APPEARANCE_AUTHORED_ROWS</c> is on (the default).
/// </summary>
public static class AugustAuthoredAppearance
{{
    /// <summary>The {items} items the boot census names, in ascending id order.</summary>
    public static readonly ImmutableArray<uint> Items = [{item_ids}];

    /// <summary>Two rows per item - one per body - in ascending row id.</summary>
    public static readonly ImmutableArray<AugustAuthoredAppearanceRow> Rows =
    [
{rows}    ];

    /// <summary>
    /// Six shader parameters per authored group: the measured ramp on branch A, and either a
    /// second measured ramp (a two-colour item) or the client's own <c>Default</c> neutral on
    /// branch B.
    /// </summary>
    public static readonly ImmutableArray<DynamicAppearanceShaderValue> ShaderValues =
    [
{values}    ];
}}
'''


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", type=Path, default=DEFAULT_DATA)
    parser.add_argument("--tints", type=Path, default=DEFAULT_TINTS)
    parser.add_argument("--out", type=Path, default=OUT_DIR)
    args = parser.parse_args()

    model_rows = datasheet(args.data / "Models.txt")
    if len(model_rows) != EXPECTED_MODEL_ROWS:
        sys.exit(f"Models.txt has {len(model_rows)} rows, expected {EXPECTED_MODEL_ROWS}")
    by_file = {row["MODEL_FILE_NAME"].lower(): row for row in model_rows}

    defaults = default_tint_row(datasheet(args.data / "TintSemanticTables.txt"))
    alpha = defaults["BaseTintMidtoneA"][3]

    loot = json.loads((args.data / "derived" / "loot.json").read_text(encoding="utf-8"))
    names = {int(item["id"]): item.get("name") or "" for item in loot["items"]}

    # measure each family's colour map once
    measured: dict[str, dict] = {}
    map_notes: list[str] = []
    for key, male_mesh, female_mesh, _ in FAMILIES:
        male_map = texture_alias(args.data / "adr" / male_mesh)
        female_map = texture_alias(args.data / "adr" / female_mesh)
        if male_map != female_map:
            sys.exit(f"{key}: the two bodies name different colour maps "
                     f"({male_map} vs {female_map}); this generator assumes one ramp per family")
        dds = args.tints / male_map
        if not dds.is_file():
            sys.exit(f"{key}: {dds} is not extracted; run pipeline.py extract")
        result = measure(dds)
        result["sha256"] = hashlib.sha256(dds.read_bytes()).hexdigest()
        measured[key] = result
        lum = result["luminance"]
        map_notes.append(
            f"//   {male_map}  {result['format']} {result['width']}x{result['height']}  "
            f"mean RGB {result['meanRgb'][0]:.0f},{result['meanRgb'][1]:.0f},"
            f"{result['meanRgb'][2]:.0f}  near-white {result['nearWhiteFraction'] * 100:.1f}%  "
            f"luminance p08/p50/p92 {lum['shadow']:.3f}/{lum['midtone']:.3f}/"
            f"{lum['highlight']:.3f}\n"
            f"//     sha256 {result['sha256']}\n")

    row_lines: list[str] = []
    value_lines: list[str] = []
    item_ids: list[int] = []

    ordinal = 0
    for key, male_mesh, female_mesh, items in FAMILIES:
        for item_id in items:
            name = names.get(item_id)
            if not name:
                sys.exit(f"item {item_id} has no name in the client's locale; cannot pick a hue")
            words, camo = colour_words(name)
            if not words:
                sys.exit(f'item {item_id} "{name}" names no colour this palette knows')

            primary = PALETTE[words[0]]
            if len(words) > 1:
                secondary = PALETTE[words[1]]
            elif camo:
                secondary = tuple(c * CAMO_SECONDARY_FACTOR for c in primary)
            else:
                secondary = None

            group = AUTHORED_GROUP_BASE + ordinal
            ramp_a = ramp(measured[key], primary, alpha)
            item_ids.append(item_id)

            for gender, mesh in ((MALE, male_mesh), (FEMALE, female_mesh)):
                model = by_file.get(mesh.lower())
                if model is None:
                    sys.exit(f"{mesh} is in no August Models.txt row")
                if int(model["GENDER"]) != gender:
                    sys.exit(f"{mesh} is Models.txt GENDER {model['GENDER']}, expected {gender}")
                row_id = AUTHORED_ROW_BASE + 2 * ordinal + (gender - 1)
                row_lines.append(
                    f"        // {item_id} {name} - {mesh}\n"
                    f"        new(0x{row_id:08X}, {item_id}, {int(model['*ID'])}, {gender}, "
                    f"0x{group:08X}),\n")

            def emit(semantic: str, value: tuple[float, float, float, float]) -> None:
                value_lines.append(
                    f"        new(0x{group:08X}, \"{semantic}\", new Vector4("
                    f"{f(value[0])}, {f(value[1])}, {f(value[2])}, {f(value[3])})),\n")

            value_lines.append(
                f"        // {item_id} {name}: {key} map p92/p50/p08 ramp x "
                f"({primary[0]:.3f}, {primary[1]:.3f}, {primary[2]:.3f})"
                f"{'' if secondary is None else ' with a second branch'}\n")
            emit("BaseTintHighlightA", ramp_a["highlight"])
            emit("BaseTintMidtoneA", ramp_a["midtone"])
            emit("BaseTintShadowA", ramp_a["shadow"])
            if secondary is None:
                emit("BaseTintHighlightB", defaults["BaseTintHighlightB"])
                emit("BaseTintMidtoneB", defaults["BaseTintMidtoneB"])
                emit("BaseTintShadowB", defaults["BaseTintShadowB"])
            else:
                ramp_b = ramp(measured[key], secondary, alpha)
                emit("BaseTintHighlightB", ramp_b["highlight"])
                emit("BaseTintMidtoneB", ramp_b["midtone"])
                emit("BaseTintShadowB", ramp_b["shadow"])

            ordinal += 1

    text = HEADER.format(
        items=len(item_ids),
        maps="".join(map_notes),
        item_ids=", ".join(str(i) for i in item_ids),
        rows="".join(row_lines),
        values="".join(value_lines))

    args.out.mkdir(parents=True, exist_ok=True)
    target = args.out / "AugustAuthoredAppearance.g.cs"
    target.write_text(text, encoding="utf-8", newline="\n")
    print(f"{target}: {len(item_ids)} item(s), {len(row_lines)} row(s), "
          f"{sum(1 for line in value_lines if line.lstrip().startswith('new('))} shader value(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
