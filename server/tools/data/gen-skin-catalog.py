#!/usr/bin/env python3
r"""
Generate src/Cranberry.Zone/AugustSkinCatalog.g.cs from the August client's own datasheets.

The Appearance grid is not the short SkinItemSlotItem prototype list. Each displayable tile is
an AcctItemConversions row whose reward belongs to an apparel/weapon class. SkinItemSlotItem
provides the category prototype: apparel families match (slot, PARAM1), weapon families match
PARAM1. The first category and first conversion for a duplicated reward win, matching the
client's keyed tables and keeping output deterministic.

Inputs are extracted from the August packs with Pack1:
  ClientItemDefinitions.txt, Models.txt, SkinItemSlot.txt, SkinItemSlotItem.txt,
  AcctItemConversions.txt

An optional, explicitly documented Z1 DynamicAppearance capture supplies missing item-to-model
links; every referenced model name is resolved through the August Models.txt table. A validated
2017 reference table repairs the capture's known bad shader-group/gender fields at generation
time. The generated server has no runtime dependency on either source.

Usage: python tools/data/gen-skin-catalog.py [data-directory] [output.cs] [appearance-capture]
                                                 [appearance-reference]
"""
import hashlib
import json
import struct
import sys
import zlib
from collections import defaultdict
from pathlib import Path

# The one datasheet reader (docs/96); this file used to carry the third private copy.
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "pipeline"))
from readers.sheet import read_table  # noqa: E402

data = Path(sys.argv[1] if len(sys.argv) > 1 else r"C:\Aug2017\out\data_aug")
dst = Path(sys.argv[2] if len(sys.argv) > 2 else
           Path(__file__).resolve().parents[2] / "src" / "Cranberry.Zone" / "AugustSkinCatalog.g.cs")
appearance_source = Path(sys.argv[3] if len(sys.argv) > 3 else
                         r"C:\Z1\Server\Data\dynamicAppearanceFriend.bin")
appearance_reference = Path(sys.argv[4] if len(sys.argv) > 4 else
                            r"C:\Z1\Data\2017\dataSources\dynamicappearance_1087.json")
appearance_packet_length = 6_625_396
appearance_packet_md5 = "59d33ecb0580512fa13c4a78d69d084b"
appearance_reference_length = 8_202_185
appearance_reference_sha256 = "f09355e681bc82149b520636b20fa97bd6486cbdce37677de2788ac217508b93"


def table(name):
    """One datasheet, header verbatim so a key column stays ``*ID`` (readers/sheet.py)."""
    try:
        return read_table(data / name)
    except ValueError as exc:
        sys.exit(str(exc))


items = {int(row["*ID"]): row for row in table("ClientItemDefinitions.txt")}
slots = table("SkinItemSlot.txt")
slot_items = table("SkinItemSlotItem.txt")
conversions = table("AcctItemConversions.txt")
models = {int(row["*ID"]): row["MODEL_FILE_NAME"] for row in table("Models.txt")}
model_names = set(models.values())


def dynamic_appearance_rows(path):
    """Read only the fixed-width item-appearance section of the locally captured Z1 table.

    August uses the same seven-dword row layout. The server repacks a filtered subset at runtime;
    the generator uses the rows only to resolve the actual male/female mesh names for equipment.
    """
    rows = defaultdict(list)
    by_id = {}
    if not path.is_file():
        return rows, by_id

    packet = zlib.decompress(path.read_bytes())
    digest = hashlib.md5(packet).hexdigest()
    if len(packet) != appearance_packet_length or digest != appearance_packet_md5:
        sys.exit(
            f"{path}: unrecognised appearance capture "
            f"(length {len(packet)}, md5 {digest})")
    if packet[:2] != b"\x17\x06":
        sys.exit(f"{path}: expected the 0x17/0x06 dynamic-appearance packet")

    count = struct.unpack_from("<I", packet, 2)[0]
    offset = 6
    end = offset + count * 28
    if end > len(packet):
        sys.exit(f"{path}: truncated item-appearance table")

    for at in range(offset, end, 28):
        row_id, _, item_id, model_id, gender_id, _, shader_group = struct.unpack_from(
            "<7I", packet, at)
        row = (row_id, model_id, gender_id, shader_group)
        rows[item_id].append(row)
        if row_id in by_id:
            sys.exit(f"{path}: duplicate item-appearance row id {row_id}")
        by_id[row_id] = (item_id, model_id, gender_id, shader_group)
    return rows, by_id


def reference_appearance_rows(path):
    """Load the smaller 2017 reference table used to repair capture row fields.

    The large capture provides coverage and shader values. Its 1,476-row overlap with this
    independently packed 2017 table has identical row ids/item mappings, but exposes bad shader
    group and zero-gender fields in the capture. Only those fixed-width row fields are consulted;
    all meshes are still resolved through August Models.txt.
    """
    if not path.is_file():
        return {}

    raw = path.read_bytes()
    digest = hashlib.sha256(raw).hexdigest()
    if len(raw) != appearance_reference_length or digest != appearance_reference_sha256:
        sys.exit(
            f"{path}: unrecognised 2017 appearance reference "
            f"(length {len(raw)}, sha256 {digest})")

    document = json.loads(raw)
    sections = (
        len(document.get("ITEM_APPEARANCE_DEFINITIONS", ())),
        len(document.get("SHADER_SEMANTIC_DEFINITIONS", ())),
        len(document.get("SHADER_PARAMETER_DEFINITIONS", ())),
    )
    if sections != (1_476, 714, 20_845):
        sys.exit(f"{path}: unexpected appearance reference census {sections}")

    rows = {}
    for wrapper in document["ITEM_APPEARANCE_DEFINITIONS"]:
        row_id = int(wrapper["ID"])
        row = wrapper["ITEM_APPEARANCE_DATA"]
        if row_id in rows:
            sys.exit(f"{path}: duplicate reference row id {row_id}")
        rows[row_id] = (
            int(row["ITEM_ID"]),
            int(row["MODEL_ID"]),
            int(row["GENDER_ID"]),
            int(row["SHADER_PARAMETER_GROUP_ID"]),
        )
    return rows


appearance_rows, appearance_rows_by_id = dynamic_appearance_rows(appearance_source)
reference_rows_by_id = reference_appearance_rows(appearance_reference)


def gender_model(raw, gender):
    if not raw:
        return ""
    name = raw.replace("<gender>", "Female" if gender == 2 else "Male")
    opposite = "Male" if gender == 2 else "Female"
    wanted = "Female" if gender == 2 else "Male"
    if opposite in name:
        candidate = name.replace(opposite, wanted)
        if candidate in model_names:
            return candidate
    return name


def appearance_model(item_id, gender):
    candidates = appearance_rows.get(item_id, ())
    # Weapon/prop meshes have no baked survivor body and are valid for both genders even when a
    # captured row's gender column is wrong (item 10 is the known duplicated-gender example).
    for _, model_id, _, _ in candidates:
        model = models.get(model_id, "")
        lowered = model.lower()
        if model and not (lowered.startswith("survivormale_")
                          or lowered.startswith("survivorfemale_")):
            return model

    for wanted_gender in (gender, 0):
        for _, model_id, declared_gender, _ in candidates:
            if declared_gender != wanted_gender:
                continue
            model = models.get(model_id, "")
            if model:
                return gender_model(model, gender)
    return ""


def visual_fields(reward, category):
    reward_row = items[reward]
    category_row = items[category]
    slot = int(reward_row["PASSIVE_EQUIP_SLOT_ID"] or
               category_row["PASSIVE_EQUIP_SLOT_ID"] or 0)
    texture = reward_row["TEXTURE_ALIAS"] or "Default"

    def model_for(gender):
        return (gender_model(reward_row["MODEL_NAME"], gender)
                or appearance_model(reward, gender)
                or gender_model(category_row["MODEL_NAME"], gender)
                or appearance_model(category, gender))

    return slot, model_for(1), model_for(2), texture


def cs_string(value):
    return value.replace("\\", "\\\\").replace('"', '\\"')

item_class_to_slot = {
    25000: 1,   # head
    25002: 2,   # chest
    25004: 3,   # back
    25005: 4,   # feet
    25003: 5,   # legs
    25008: 6,   # hands
    25040: 8,   # face
    25045: 9,   # eyes
    25041: 10,  # body armour
}
weapon_classes = {4096, 4098, 25036, 25037, 25038}

slot_types = {int(row["*SLOT_ID"]): int(row["SLOT_TYPE_ID"]) for row in slots}
apparel_families = defaultdict(list)
weapon_families = {}
for row in slot_items:
    slot = int(row["SKIN_ITEM_SLOT_ID"])
    prototype = int(row["PROTOTYPE_ITEM_ID"])
    prototype_row = items.get(prototype)
    if not prototype_row:
        continue
    param1 = int(prototype_row["PARAM1"])
    if slot_types.get(slot) == 1:
        apparel_families[(slot, param1)].append(prototype)
    elif slot_types.get(slot) == 2 and param1:
        weapon_families.setdefault(param1, prototype)

apparel = []
weapons = []
seen_rewards = set()
category_resolution = defaultdict(int)
for row in conversions:
    reward = int(row["REWARD_ITEM_ID"])
    account = int(row["ACCOUNT_ITEM_ID"])
    item = items.get(reward)
    if not reward or not account or item is None or reward in seen_rewards:
        continue
    seen_rewards.add(reward)

    item_class = int(item["ITEM_CLASS"])
    param1 = int(item["PARAM1"])
    if item_class in item_class_to_slot:
        slot = item_class_to_slot[item_class]
        candidates = apparel_families.get((slot, param1), ())
        exact = [prototype for prototype in candidates
                 if items[prototype]["DESCRIPTION_ID"] == item["DESCRIPTION_ID"]]
        if len(exact) > 1:
            sys.exit(
                f"ambiguous apparel category for reward {reward}: description "
                f"{item['DESCRIPTION_ID']} matches prototypes {exact}")
        if exact:
            category = exact[0]
            category_resolution["exact-description"] += 1
        elif candidates:
            # The client has hundreds of colour variants whose descriptive string is unique to
            # the reward. Preserve SkinItemSlotItem order as the deterministic family fallback.
            category = candidates[0]
            category_resolution["ordered-family-fallback"] += 1
        else:
            category = None
            category_resolution["unmapped"] += 1
        if category:
            apparel.append((category, reward, account))
    elif item_class in weapon_classes:
        category = weapon_families.get(param1)
        if category:
            weapons.append((category, reward, account))

known = {(reward, account) for _, reward, account in apparel + weapons}
for pair in ((2158, 1856), (2038, 2022), (3250, 3262), (2229, 3698)):
    if pair not in known:
        sys.exit(f"conversion self-check failed: wearable/account pair {pair} is missing")

categories_by_reward = {reward: category for category, reward, _ in apparel}
for reward in (3933, 3862):
    if categories_by_reward.get(reward) != 2827:
        sys.exit(
            f"category self-check failed: reward {reward} should resolve to helmet prototype "
            f"2827, got {categories_by_reward.get(reward)}")

if (len(apparel), len(weapons)) != (609, 69):
    sys.exit(
        f"unexpected August catalogue census: {len(apparel)} apparel / {len(weapons)} weapons; "
        "expected 609 / 69")


def baked_gender(model_id):
    """Infer body gender only from an August model's baked filename."""
    model = models.get(model_id, "").lower()
    if model.startswith("survivormale_"):
        return 1
    if model.startswith("survivorfemale_"):
        return 2
    return 0


def build_appearance_row_overrides(catalogue):
    """Return August-scoped fixed-width corrections for the validated captured table.

    Group ids use the overlapping 2017 reference row when available. A zero capture gender uses
    that row's declared gender, then the August Models.txt filename when the smaller reference
    has no answer. Finally, a duplicated genderless mesh pair is split across both genders. This
    reproduces the client-valid data repair without shipping either source file with the server.
    """
    wanted_items = {value for category, reward, _ in catalogue for value in (category, reward)}
    resolved = {}
    by_item = defaultdict(list)
    corrections = defaultdict(int)

    for row_id, (item_id, model_id, gender_id, shader_group) in appearance_rows_by_id.items():
        if item_id not in wanted_items:
            continue

        gender = gender_id
        group = shader_group
        reference = reference_rows_by_id.get(row_id)
        if reference is not None and reference[0] == item_id:
            if reference[3] != group:
                group = reference[3]
                corrections["shader-group"] += 1
            if gender == 0 and reference[2] != 0:
                gender = reference[2]
                corrections["reference-gender"] += 1

        if gender == 0:
            inferred = baked_gender(model_id)
            if inferred:
                gender = inferred
                corrections["august-model-gender"] += 1

        resolved[row_id] = [item_id, model_id, gender, group, gender_id, shader_group]
        by_item[item_id].append(row_id)

    # A genderless weapon mesh legitimately serves both bodies, but the captured table contains
    # one duplicated two-row item where both rows declare the same body. Split only that exact
    # structural anomaly; normal one-row genderless models stay gender 0.
    for item_id, row_ids in by_item.items():
        if len(row_ids) != 2:
            continue
        first = resolved[row_ids[0]]
        second = resolved[row_ids[1]]
        if (first[1] == second[1]
                and first[2] == second[2]
                and first[2] in (1, 2)):
            second[2] = 2 if first[2] == 1 else 1
            corrections["split-duplicate-gender"] += 1

    overrides = []
    for row_id, (item_id, _, gender, group, original_gender, original_group) in resolved.items():
        if gender != original_gender or group != original_group:
            overrides.append((row_id, item_id, group, gender))

    expected = {
        "shader-group": 51,
        "reference-gender": 40,
        "august-model-gender": 60,
        "split-duplicate-gender": 1,
    }
    if appearance_rows_by_id and reference_rows_by_id and dict(corrections) != expected:
        sys.exit(
            f"unexpected August appearance correction census: {dict(corrections)}; "
            f"expected {expected}")
    return sorted(overrides), corrections


appearance_overrides, appearance_corrections = build_appearance_row_overrides(apparel + weapons)

out = [
    "// <auto-generated>",
    "// Generated by tools/data/gen-skin-catalog.py from the August client's own",
    "// ClientItemDefinitions, Models, SkinItemSlot, SkinItemSlotItem and AcctItemConversions tables.",
    "// Missing model links use validated captured rows, resolved back through August Models.txt.",
    "// Fixed-width appearance corrections are derived from the validated 2017 overlap and August models.",
    "// H1Z1.exe 0.0.118.208059. Do not edit.",
    "// </auto-generated>",
    "",
    "#nullable enable",
    "",
    "namespace Cranberry.Zone;",
    "",
    "public readonly record struct AugustSkinCatalogEntry(",
    "    uint CategoryPrototypeId,",
    "    uint RewardItemId,",
    "    uint AccountItemId,",
    "    uint EquipmentSlotId,",
    "    string MaleModelName,",
    "    string FemaleModelName,",
    "    string TextureAlias)",
    "{",
    "    public string ModelNameFor(uint gender) => gender == 2 ? FemaleModelName : MaleModelName;",
    "}",
    "",
    "public readonly record struct AugustAppearanceRowOverride(",
    "    uint ItemDefinitionId,",
    "    uint ShaderParameterGroupId,",
    "    uint GenderId);",
    "",
    "/// <summary>Every conversion-backed skin the August client can place in an Appearance grid.</summary>",
    "public static class AugustSkinCatalog",
    "{",
]


def emit(name, rows):
    out.extend([
        f"    public static readonly IReadOnlyList<AugustSkinCatalogEntry> {name} =",
        "    [",
    ])
    for category, reward, account in rows:
        slot, male, female, texture = visual_fields(reward, category)
        if not male or not female or not texture:
            sys.exit(
                f"visual self-check failed for reward {reward}: slot={slot}, "
                f"male={male!r}, female={female!r}, texture={texture!r}")
        out.append(
            f'        new({category}, {reward}, {account}, {slot}, "{cs_string(male)}", '
            f'"{cs_string(female)}", "{cs_string(texture)}"),')
    out.extend(["    ];", ""])


emit("Apparel", apparel)
emit("Weapons", weapons)

out.extend([
    "    /// <summary>Corrections applied to validated captured appearance rows before filtering.</summary>",
    "    public static readonly IReadOnlyDictionary<uint, AugustAppearanceRowOverride> AppearanceRowOverrides =",
    "        new Dictionary<uint, AugustAppearanceRowOverride>",
    "        {",
])
for row_id, item_id, shader_group, gender_id in appearance_overrides:
    out.append(
        f"            [{row_id}] = new({item_id}, {shader_group}, {gender_id}),")
out.extend(["        };", ""])
out.extend(["}", ""])

dst.write_text("\n".join(out), encoding="utf-8", newline="\n")
print(
    f"wrote {dst} ({len(apparel)} apparel, {len(weapons)} weapons; "
    f"categories: {dict(category_resolution)}; "
    f"appearance meshes from {appearance_source if appearance_rows else 'none'}; "
    f"{len(appearance_overrides)} row overrides {dict(appearance_corrections)})")
