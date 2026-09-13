#!/usr/bin/env python3
r"""gen-appearance-catalog - build the APPEARANCE lane's two generated catalogues.

WHY THIS TOOL EXISTS (docs/54)

  Two owner-reported symptoms need data the August client already holds and that Cranberry
  already reads and throws away.

  A. "The AR-15 should have green boxes but it's brown, the shotgun is white."
     docs/54 s1.4: a ground prop has NO colour lever on the wire - AddLightweightNpc
     carries no shader group and no appearance ids, and every .adr in the roster declares
     one texture alias (``default0``) and an empty <TintAliases/>. So a ground item's
     colour is decided entirely by its Models.txt row, and the only defence against
     sending the wrong row - or one that does not ship at all, which is what
     ``Common_Props_AmmoBoxes_Shotgun.adr`` (10137) is - is a catalogue of every row in
     Models.txt together with whether that file is actually in the client's asset packs.
     That is AugustModelCatalog.g.cs.

  B. "I pick items up and they go into the slots but they don't show on me."
     docs/54 s3: ZoneService.MeshFor resolves the third-person mesh only from
     ClientItemDefinitions.MODEL_NAME, which is blank for every wearable in the loot
     roster, so the attachment is never emitted. The mesh the client wants is the
     ModelId column of the dynamic-appearance table, resolved through August Models.txt.
     That is AugustWornMeshCatalog.g.cs.

INPUT
    ``C:\Aug2017\out\data_aug\Models.txt``            the client's own actor catalogue,
                                                      1,173 rows (August 0.0.118.208059)
    ``C:\Aug2017\out\data_aug\pack-index-aug.tsv``    index of all 256 Assets_*.pack
                                                      archives, 50,502 assets
    ``C:\Aug2017\out\appearance-tints\
       z1-appearance-parsed.json``                    the APPEARANCE lane's DERIVE-phase
                                                      parse of the D22-gated dynamic-appearance
                                                      source (inflated 6,625,396 bytes, md5
                                                      59d33ecb0580512fa13c4a78d69d084b - the same
                                                      gate AugustDynamicAppearanceTable enforces
                                                      at runtime). Rows are
                                                      [RowId, DataId, ItemId, ModelId, GenderId,
                                                      ClientRequirementId, ShaderParameterGroupId].
    ``src/Cranberry.Zone/Data/Loot/z2-loot-tables.json``   the item roster to cover

OUTPUT
    ``src/Cranberry.Zone/Appearance/AugustModelCatalog.g.cs``
    ``src/Cranberry.Zone/Appearance/AugustWornMeshCatalog.g.cs``

  EVERY model name is resolved through the August Models.txt by id and checked against the
  August pack index; nothing is hard-coded and nothing is carried over from the source blob
  except the (item -> model id, gender, shader group) association itself. The generated
  server has no runtime dependency on either source file.

USAGE
    python tools/appearance/gen-appearance-catalog.py [--check]
"""

from __future__ import annotations

import argparse
import csv
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_DATA = Path(r"C:\Aug2017\out\data_aug")
DEFAULT_APPEARANCE = Path(r"C:\Aug2017\out\appearance-tints\z1-appearance-parsed.json")
DEFAULT_LOOT = ROOT / "src" / "Cranberry.Zone" / "Data" / "Loot" / "z2-loot-tables.json"
OUT_DIR = ROOT / "src" / "Cranberry.Zone" / "Appearance"

# Census assertions. This tool transcribes one specific client build; a silent drift in any
# of these means it is transcribing something else, and the build should fail, not adapt.
EXPECTED_MODEL_ROWS = 1173
EXPECTED_PACK_ASSETS = 50502
EXPECTED_APPEARANCE_ROWS = 3551

MALE = 1
FEMALE = 2

#: roster items whose captured worn row names a mesh the August build does not ship.
#: See the use site for why these are UNCOVERED rather than a build failure.
NO_AUGUST_MESH = {
    2246: "Crossbow (D273): the 1087 capture names ModelId 10935, which is not an August "
          "Models.txt row. It is a ground pickup and a held weapon; neither needs a worn mesh.",
}


def datasheet(path: Path) -> list[dict[str, str]]:
    lines = path.read_text(encoding="latin-1").splitlines()
    if not lines or not lines[0].startswith("#"):
        sys.exit(f"{path}: missing datasheet header")
    header = lines[0].lstrip("#").rstrip("^").split("^")
    return list(csv.DictReader(lines[1:], fieldnames=header, delimiter="^"))


def cs_string(value: str) -> str:
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", type=Path, default=DEFAULT_DATA)
    parser.add_argument("--appearance", type=Path, default=DEFAULT_APPEARANCE)
    parser.add_argument("--loot", type=Path, default=DEFAULT_LOOT)
    parser.add_argument("--out", type=Path, default=OUT_DIR)
    parser.add_argument("--check", action="store_true",
                        help="fail instead of writing when the output would change")
    args = parser.parse_args()

    models_rows = datasheet(args.data / "Models.txt")
    if len(models_rows) != EXPECTED_MODEL_ROWS:
        sys.exit(f"Models.txt has {len(models_rows)} rows, expected {EXPECTED_MODEL_ROWS}")
    models: dict[int, str] = {}
    for row in models_rows:
        model_id = int(row["*ID"])
        if model_id in models:
            sys.exit(f"Models.txt row {model_id} is duplicated")
        models[model_id] = row["MODEL_FILE_NAME"]

    pack_lines = (args.data / "pack-index-aug.tsv").read_text(
        encoding="latin-1").splitlines()[1:]
    if len(pack_lines) != EXPECTED_PACK_ASSETS:
        sys.exit(f"pack index has {len(pack_lines)} assets, expected {EXPECTED_PACK_ASSETS}")
    packed = {line.split("\t")[0].lower() for line in pack_lines}

    appearance = json.loads(args.appearance.read_text(encoding="utf-8"))["app"]
    if len(appearance) != EXPECTED_APPEARANCE_ROWS:
        sys.exit(f"appearance source has {len(appearance)} rows, "
                 f"expected {EXPECTED_APPEARANCE_ROWS}")

    loot = json.loads(args.loot.read_text(encoding="utf-8"))
    roster: set[int] = set()
    for category in loot["categories"]:
        for entry in category["entries"]:
            roster.add(int(entry["itemDefinitionId"]))
    for box in loot["clusters"]["boxes"]:
        roster.add(int(box["itemDefinitionId"]))
        roster.add(int(box["weaponItemDefinitionId"]))

    by_item: dict[int, list[list[int]]] = {}
    for row in appearance:
        by_item.setdefault(int(row[2]), []).append([int(v) for v in row])

    worn: list[tuple[int, int, int, str, str, int]] = []
    for item_id in sorted(roster):
        rows = by_item.get(item_id)
        if not rows:
            continue
        male = next((r for r in rows if r[4] == MALE), None)
        female = next((r for r in rows if r[4] == FEMALE), None)
        # The source's one genuine gender defect (docs/54 s5.1): appearance rows 181 and 182
        # both carry GenderId 2, so item 10 (AR-15) resolves no male row. Both rows name the
        # same ModelId, so falling back to the other gender's row is exact rather than a guess -
        # and the two checks below refuse the fallback the moment it would change the MESH or
        # the SHADER GROUP.
        #
        # The ModelId check is new (wave-5 verify pass). This comment previously promised a
        # safety net that did not exist: the only check was `male[6] != female[6]`, which
        # compares the shader group at index 6 and says nothing about the ModelId at index 3.
        # It happens to be harmless today - rows 181 and 182 both name 9591 - but "happens to
        # be" is precisely what a generator check is for.
        male = male or rows[0]
        female = female or rows[0]
        male_file = models.get(male[3])
        female_file = models.get(female[3])
        if not male_file or not female_file:
            # D273 (lane LOOT, 2026-09-03): the Crossbow joined the ground roster, and the 1087-era
            # appearance capture DOES carry a worn row for it - naming ModelId 10935, which is not a
            # Models.txt row in the August build at all. That is not a defect in this generator and
            # it is not something a guess can fix: for August purposes an item whose captured mesh
            # does not exist here is exactly as UNCOVERED as one with no captured row, which the
            # `if not rows: continue` above already skips, and `uncovered` below already reports.
            #
            # The exception is NAMED rather than general on purpose. A silent skip would turn this
            # check into a no-op for every future roster addition, which is the opposite of what it
            # is for; any item that is not in this dict still stops the build.
            if item_id in NO_AUGUST_MESH:
                print(f"  note: item {item_id} skipped - {NO_AUGUST_MESH[item_id]}")
                continue

            sys.exit(f"item {item_id}: model {male[3]}/{female[3]} is not in August Models.txt")
        if male_file.lower() not in packed or female_file.lower() not in packed:
            sys.exit(f"item {item_id}: {male_file}/{female_file} is in no August asset pack")
        if male[3] != female[3] and (
                next((r for r in rows if r[4] == MALE), None) is None
                or next((r for r in rows if r[4] == FEMALE), None) is None):
            sys.exit(
                f"item {item_id}: a gender fell back to the other gender's row and the two rows "
                f"name DIFFERENT meshes ({male[3]} vs {female[3]}) - the fallback would be a guess")
        if male[6] != female[6]:
            sys.exit(f"item {item_id}: genders disagree on shader group {male[6]} vs {female[6]}")
        worn.append((item_id, male[3], female[3], male_file, female_file, male[6]))

    uncovered = sorted(roster - {row[0] for row in worn})
    catalogue_rows = sorted(models.items())

    model_lines = "\n".join(
        f"        new({model_id}, {cs_string(name)}, "
        f"{'true' if name.lower() in packed else 'false'}),"
        for model_id, name in catalogue_rows)
    shippable = sum(1 for _, name in catalogue_rows if name.lower() in packed)
    absent = EXPECTED_MODEL_ROWS - shippable

    model_cs = MODEL_TEMPLATE.format(
        rows=EXPECTED_MODEL_ROWS,
        assets=EXPECTED_PACK_ASSETS,
        assets_grouped=f"{EXPECTED_PACK_ASSETS:,}",
        shippable=shippable,
        absent=absent,
        model_lines=model_lines)

    worn_lines = "\n".join(
        f"        new({item_id}, {male_id}, {female_id}, {cs_string(male_file)}, "
        f"{cs_string(female_file)}, {group}),"
        for item_id, male_id, female_id, male_file, female_file, group in worn)

    worn_cs = WORN_TEMPLATE.format(
        covered=len(worn),
        roster=len(roster),
        uncovered=len(uncovered),
        worn_lines=worn_lines)

    args.out.mkdir(parents=True, exist_ok=True)
    changed = False
    for name, text in (("AugustModelCatalog.g.cs", model_cs),
                       ("AugustWornMeshCatalog.g.cs", worn_cs)):
        path = args.out / name
        previous = path.read_text(encoding="utf-8") if path.exists() else None
        if previous == text:
            continue
        changed = True
        if args.check:
            print(f"{path}: would change", file=sys.stderr)
        else:
            path.write_text(text, encoding="utf-8", newline="\n")
            print(f"wrote {path}")

    print(f"models: {EXPECTED_MODEL_ROWS} rows, {shippable} shippable, "
          f"{absent} named but absent from every pack")
    print(f"worn:   {len(worn)} of {len(roster)} roster items resolved; "
          f"{len(uncovered)} have no appearance row: {uncovered}")
    return 1 if (changed and args.check) else 0


MODEL_TEMPLATE = r'''// <auto-generated>
// Generated by tools/appearance/gen-appearance-catalog.py from the August client's own
// Models.txt ({rows} rows) checked against an index of all 256 Assets_*.pack archives
// ({assets_grouped} assets). H1Z1.exe 0.0.118.208059. Do not edit.
// </auto-generated>

#nullable enable

namespace Cranberry.Zone.Appearance;

/// <summary>
/// One row of the August client's <c>Models.txt</c>: the actor id the server puts on the wire,
/// the file the client will try to load for it, and whether that file is actually present in
/// the client's asset packs.
/// </summary>
/// <param name="ModelId"><c>Models.txt</c> <c>*ID</c> - the value carried by
/// <c>AddLightweightNpc</c> at <c>+0x40</c> and by the door and vehicle spawners.</param>
/// <param name="FileName"><c>MODEL_FILE_NAME</c>, verbatim.</param>
/// <param name="Ships">
/// <c>true</c> when a file of that name is in one of the 256 <c>Assets_*.pack</c> archives.
/// <c>false</c> means the sheet names an actor the client cannot load: the client answers
/// <c>Failed to load asset '...'</c> in its own <c>Logs\AssetFailure.log</c> and draws nothing
/// usable. {absent} of the {rows} rows are in that state - a gap in the shipped client, not a
/// Cranberry defect, but fatal the moment the server names one of them (docs/54 s2.3).
/// </param>
public readonly record struct AugustModelRow(uint ModelId, string FileName, bool Ships);

/// <summary>
/// The August client's complete actor catalogue, with shippability. This is the authority for
/// every <c>Models.txt</c> row id Cranberry puts on the wire - ground loot, doors, vehicles and
/// worn meshes alike - and it exists so that naming an actor the client cannot load is a failing
/// test rather than a white box on the floor (docs/54 I2).
/// </summary>
public static class AugustModelCatalog
{{
    /// <summary>Rows in the August <c>Models.txt</c>.</summary>
    public const int RowCount = {rows};

    /// <summary>Rows whose file is present in the client's asset packs.</summary>
    public const int ShippableRowCount = {shippable};

    /// <summary>Assets indexed across all 256 <c>Assets_*.pack</c> archives.</summary>
    public const int IndexedAssetCount = {assets};

    public static readonly IReadOnlyList<AugustModelRow> Rows =
    [
{model_lines}
    ];

    private static readonly Dictionary<uint, AugustModelRow> ById =
        Rows.ToDictionary(row => row.ModelId);

    /// <summary>
    /// The row for <paramref name="modelId"/>, or <c>false</c> when the August
    /// <c>Models.txt</c> has no such id at all.
    /// </summary>
    public static bool TryGet(uint modelId, out AugustModelRow row) =>
        ById.TryGetValue(modelId, out row);

    /// <summary>
    /// The file name for <paramref name="modelId"/>, or the empty string when the id is unknown.
    /// </summary>
    public static string FileNameFor(uint modelId) =>
        ById.TryGetValue(modelId, out AugustModelRow row) ? row.FileName : string.Empty;

    /// <summary>
    /// <c>true</c> when the id resolves <em>and</em> the client can load the file it names. This
    /// is the predicate every server-chosen actor id has to satisfy.
    /// </summary>
    public static bool CanLoad(uint modelId) =>
        ById.TryGetValue(modelId, out AugustModelRow row) && row.Ships;
}}
'''

WORN_TEMPLATE = r'''// <auto-generated>
// Generated by tools/appearance/gen-appearance-catalog.py. The item-to-model association is the
// ModelId column of the D22-gated dynamic-appearance source (inflated 6,625,396 bytes, md5
// 59d33ecb0580512fa13c4a78d69d084b - the same gate AugustDynamicAppearanceTable enforces at
// runtime); every model id is resolved to a file through the August Models.txt and every file is
// confirmed present in the August asset packs. H1Z1.exe 0.0.118.208059. Do not edit.
// </auto-generated>

#nullable enable

namespace Cranberry.Zone.Appearance;

/// <summary>
/// The third-person mesh the August client wants on a character for one item, per gender, and
/// the shader parameter group that colours it.
/// </summary>
/// <param name="ItemDefinitionId"><c>ClientItemDefinitions</c> <c>*ID</c>.</param>
/// <param name="MaleModelId">Male <c>Models.txt</c> row.</param>
/// <param name="FemaleModelId">Female <c>Models.txt</c> row.</param>
/// <param name="MaleModelName">Male <c>.adr</c>, resolved through August <c>Models.txt</c>.</param>
/// <param name="FemaleModelName">Female <c>.adr</c>.</param>
/// <param name="ShaderParameterGroupId">
/// The colourway. docs/54 s1.3: this is the <em>only</em> live colour lever in this build - the
/// same mesh is Green (804), Black (805) and Red (812) backpack and nothing but this id tells
/// them apart. Wave 8 (docs/80 edit 3) puts it on the wire for a worn attachment when - and only
/// when - the transmitted appearance table defines parameter rows for the group, which is what
/// <c>AugustDynamicAppearanceTable.DefinesShaderGroup</c> answers; the 26 wearables the filter
/// discards still travel at 0, so docs/54 I4 remains its own experiment.
/// </param>
public readonly record struct AugustWornMesh(
    uint ItemDefinitionId,
    uint MaleModelId,
    uint FemaleModelId,
    string MaleModelName,
    string FemaleModelName,
    uint ShaderParameterGroupId)
{{
    /// <summary>The mesh for a body of <paramref name="gender"/> (2 = female).</summary>
    public string ModelNameFor(uint gender) =>
        gender == CharacterVisuals.Female ? FemaleModelName : MaleModelName;
}}

/// <summary>
/// Every item in Cranberry's Z2 loot roster that the dynamic-appearance table gives a worn mesh
/// for - {covered} of the {roster} distinct ids in <c>z2-loot-tables.json</c>.
/// <para>
/// The other {uncovered} are ammunition, throwables, medical items, duct tape, the waist pack and
/// the machete, and every one of those that can be held carries a
/// <c>ClientItemDefinitions.MODEL_NAME</c> instead. The two resolvers are exactly complementary,
/// which is why <see cref="AugustWornVisuals"/> tries this one first and falls back to the sheet
/// (docs/54 s3.3).
/// </para>
/// </summary>
public static class AugustWornMeshCatalog
{{
    /// <summary>Distinct item ids in <c>z2-loot-tables.json</c> at generation time.</summary>
    public const int RosterItemCount = {roster};

    public static readonly IReadOnlyList<AugustWornMesh> Entries =
    [
{worn_lines}
    ];

    private static readonly Dictionary<uint, AugustWornMesh> ByItem =
        Entries.ToDictionary(entry => entry.ItemDefinitionId);

    /// <summary>The worn mesh for one item, or <c>false</c> when the appearance table has no
    /// row for it.</summary>
    public static bool TryGet(uint itemDefinitionId, out AugustWornMesh mesh) =>
        ByItem.TryGetValue(itemDefinitionId, out mesh);
}}
'''


if __name__ == "__main__":
    raise SystemExit(main())
