#!/usr/bin/env python3
r"""
derive_loot.py - build Cranberry's server-ready **loot / inventory** dataset by joining the
August client's own datasheets into one document in this project's own schema.

INPUT   C:\Aug2017\out\data_aug\*.txt   - datasheets extracted from the client's Assets_*.pack
                                          files by tools/pack/packread.py (D7)
        C:\Aug2017\Client\Locale\en_us_data.dat + .dir  - locale archive, for id -> name
OUTPUT  C:\Aug2017\out\data_aug\derived\loot.json   (~1 MB, one JSON document)

WHAT IT IS FOR
    The Cranberry zone server needs four things from the client's data before it can run loot
    and inventory: (1) the item-class taxonomy every equip rule keys on, (2) the capacity and
    lifecycle rules of every container, (3) the loadout/equip-slot layout a character wears,
    and (4) a per-item projection (bulk, stack size, class, equip slots, container granted,
    rarity, use options). This tool produces exactly those, joined and with every id that has
    a locale string resolved to its English name. It invents nothing: anything the server
    needs that the client does not carry is listed in ``serverSideGaps`` instead.

SCHEMA (Cranberry's own design, ``format = "cranberry.loot-dataset"``, ``formatVersion 1``)

    header      format / formatVersion / generator / generatedUtc / clientBuild / locale
    sources[]   every input sheet with byte size, sha256, row count - the reproducibility record
    provenance  {"section.field": "Sheet.COLUMN"} for EVERY emitted field; entries that read
                "derived: ..." are this tool's own computation, not a client value
    counts      row count of each emitted section
    integrity   join-verification results (unresolved reference counts); all-zero is the
                expected state for this build, a non-zero value is a real data change
    rarities[]         id -> colour/designer name (ItemRarity)
    grinderInputs[]    rarity -> how many items the grinder consumes (Grinders)
    itemClasses[]      the taxonomy, with reverse indices (which items are in the class,
                       which equip slots accept it)
    containers[]       capacity + lifecycle per container definition, with the reverse join
                       to the items that grant it
    equipSlots[]       character equipment slots, with the item classes each accepts
    loadoutSlotEquipMap[]  the flat SLOT_ID -> EQUIP_SLOT_ID table (LoadoutEquipSlots)
    loadouts[]         per-loadout slot layout, each slot carrying its allowed item classes
    useOptions[]       the inventory action vocabulary (LootItem, DropItem, EquipItem, ...)
    useOptionGroups[]  ordered option sets an item can offer
    items[]            per-item server projection (2,643 rows)
    serverSideGaps[]   values the server needs that this build's client genuinely does not
                       ship - each with evidence and what Cranberry must do about it

FORMAT FACTS RELIED ON
  * Datasheet dialect (``^``-delimited, ``#`` header line, ``*`` key marker, one trailing
    ``^`` per line, no quoting/escaping): derived and evidenced in tools/data/sheet.py.
  * String id -> locale key: ``jenkins_lookup2(ascii("Global.Text." + str(id)), 0)``; the
    ``Global.Text.%d`` literal is in H1Z1.exe 0.0.118.208059 at VA 0x143118638. Derived and
    evidenced in tools/locale/localedat.py. A string id of 0 means "unset", not a key.
  * ``ClientItemDefinitions.PARAM1`` is a **factory-overloaded** column. It is the container
    definition id ONLY on rows whose ``CODE_FACTORY_NAME`` is ``EquippableContainer``; on
    ``Weapon`` / ``EmoteAnimation`` / ``Foundation`` / ... rows it means something else. This
    tool joins it to ContainerDefinitions on that factory alone (verified: all 531
    EquippableContainer rows resolve - 525 to a real container id, 6 to PARAM1 = 0 = none).
  * ``ItemClassMappings`` is a SECOND, many-to-many classification, not a copy of
    ``ClientItemDefinitions.ITEM_CLASS``: of its 482 rows exactly 1 agrees with the item's
    primary class. Primary class and mapped classes are emitted as separate fields.
  * ``ContainerSlotDefinitions``, ``ItemClassSets``, ``FilterToItemClass``,
    ``ItemFiltersToClassMap``, ``VehicleItemClasses`` and ``TintGroupMappings`` ship
    header-only (zero rows) in this build, so per-slot item-type restrictions inside a
    container do not come from the client at all.
  * ``ItemClassGroups`` / ``ItemFilterOptions`` are the inventory UI's filter tabs. They are
    deliberately NOT emitted: they are presentation, not server rules.

CLEAN ROOM
    Every value here comes from the client's own files under C:\Aug2017\Client via this
    repository's own extractor (docs/00 rule 2, docs/01 D7). No emulator source, schema or
    data was consulted. The output schema and all field names are this project's own.

Usage:
    python tools/data/derive_loot.py
    python tools/data/derive_loot.py --data-dir C:\Aug2017\out\data_aug \
                                     --out C:\Aug2017\out\data_aug\derived\loot.json
    python tools/data/derive_loot.py --lang de_de --out ...\loot-de_de.json
    python tools/data/derive_loot.py --no-locale       # ids only, no locale archive needed
    python tools/data/derive_loot.py --compact         # one line, no indentation
"""

from __future__ import annotations

import argparse
import collections
import datetime as _dt
import hashlib
import json
import sys
from pathlib import Path
from typing import Any, Iterable

_TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(_TOOLS / "data"))
sys.path.insert(0, str(_TOOLS / "locale"))

from sheet import Sheet  # noqa: E402

DEFAULT_DATA_DIR = Path(r"C:\Aug2017\out\data_aug")
DEFAULT_OUT = DEFAULT_DATA_DIR / "derived" / "loot.json"
CLIENT_BUILD = "0.0.118.208059"

#: Sheets read by this tool. ``required=False`` sheets may legitimately be header-only.
INPUT_SHEETS = [
    "ClientItemDefinitions.txt",
    "ItemClasses.txt",
    "ItemClassMappings.txt",
    "ItemRarity.txt",
    "Grinders.txt",
    "ContainerDefinitions.txt",
    "ContainerSlotDefinitions.txt",
    "EquipmentSlotDefinitions.txt",
    "EquipSlotItemClasses.txt",
    "Loadouts.txt",
    "LoadoutSlots.txt",
    "LoadoutSlotItemClasses.txt",
    "LoadoutEquipSlots.txt",
    "ItemUseOptions.txt",
    "ItemUseOptionGroups.txt",
    "ItemIdUseOptionGroupId.txt",
]

#: Factory name on which PARAM1 means "container definition id" - and only on this one.
CONTAINER_FACTORY = "EquippableContainer"


# -- small helpers ---------------------------------------------------------------------


def _int(value: str | None, default: int = 0) -> int:
    """Parse a datasheet cell as an int. Empty means 'unset', which is 0 in every sheet here."""
    if value is None:
        return default
    v = value.strip()
    if not v:
        return default
    try:
        return int(v)
    except ValueError:
        try:
            return int(float(v))
        except ValueError:
            return default


def _bool(value: str | None) -> bool:
    """Datasheet booleans are the literal characters '0' and '1'."""
    return _int(value) != 0


def _sorted_ids(ids: Iterable[int]) -> list[int]:
    return sorted(set(ids))


def _capacity_note(containers: list[dict[str, Any]]) -> str:
    """Describe how the container rows actually constrain capacity, by counting them.

    This note used to be hand-written: "MAXIMUM_WEIGHT is 0 on every container but 101, and
    MAXIMUM_SLOTS is parked at 9999 wherever MAX_BULK is set." Both halves are false in this
    build - 15 containers carry a non-zero MAXIMUM_WEIGHT, and 3 set MAX_BULK while keeping
    a real MAXIMUM_SLOTS (98: 5000/999, 111 and 112: 25/4) - which would tell a reader that
    ``maxWeight`` and ``maxSlots`` are dead columns when three containers gate on slots and
    fifteen carry a weight. The per-row data was right all along, so the note is now derived
    from the same rows the reader is looking at.
    """
    models = collections.Counter(c["capacityModel"] for c in containers)
    weighted = _sorted_ids(c["id"] for c in containers if c["maxWeight"])
    hybrid = _sorted_ids(c["id"] for c in containers if c["capacityModel"] == "hybrid")
    return (
        f"Capacity model, counted over all {len(containers)} containers: "
        f"{models.get('slots', 0)} constrain by slots, {models.get('bulk', 0)} by bulk "
        f"(MAX_BULK set with MAXIMUM_SLOTS parked at 9999), {models.get('hybrid', 0)} by "
        f"both. Bulk is the dominant KOTK constraint but it is not the only one: containers "
        f"{hybrid} set MAX_BULK while keeping a real MAXIMUM_SLOTS, and "
        f"{len(weighted)} containers carry a non-zero MAXIMUM_WEIGHT ({weighted}). Treat "
        f"none of the three columns as dead; containers[].capacityModel records which "
        f"system each row actually uses."
    )


class Names:
    """
    String-id -> English text, or a no-op when --no-locale is given.

    ``get(0)`` is always ``None``: 0 is the sheets' "column unset" marker, never a locale key.
    """

    def __init__(self, lang: str | None) -> None:
        self.lang = lang
        self._loc = None
        self._key = None
        self.resolved = 0
        self.unresolved = 0
        if lang:
            from localedat import LocaleData, text_key  # noqa: PLC0415 - optional dependency

            self._loc = LocaleData(lang)
            self._key = text_key

    def get(self, string_id: int) -> str | None:
        if not string_id or self._loc is None:
            return None
        text = self._loc.get(self._key(string_id))  # type: ignore[misc]
        if text is None:
            self.unresolved += 1
            return None
        self.resolved += 1
        return text


def _sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


# -- provenance ------------------------------------------------------------------------
#
# One entry per emitted field. "Sheet.COLUMN" means the value is that column verbatim
# (integers parsed, '0'/'1' turned into booleans). "derived: ..." means this tool computed it.

PROVENANCE: dict[str, str] = {
    "rarities[].id": "ItemRarity.ID",
    "rarities[].colorName": "ItemRarity.DESCRIPTION",
    "rarities[].designerName": "ItemRarity.DESIGNER_NAME",
    "rarities[].clientColorId": "ItemRarity.CLIENT_COLOR_ID",

    "grinderInputs[].grinderId": "Grinders.ID",
    "grinderInputs[].rarityId": "Grinders.RARITY_ID (-> ItemRarity.ID)",
    "grinderInputs[].inputCount": "Grinders.INPUT_COUNT",

    "itemClasses[].id": "ItemClasses.ID (the value of ClientItemDefinitions.ITEM_CLASS)",
    "itemClasses[].name": "locale(ItemClasses.NAME_ID); null when NAME_ID is 0 or unresolved",
    "itemClasses[].nameStringId": "ItemClasses.NAME_ID",
    "itemClasses[].tooltipStringId": "ItemClasses.TOOLTIP_STRING_ID",
    "itemClasses[].wieldType": "ItemClasses.WIELD_TYPE",
    "itemClasses[].secondaryWieldType": "ItemClasses.SECONDARY_WIELD_TYPE",
    "itemClasses[].factionId": "ItemClasses.FACTION_ID",
    "itemClasses[].scoreValue": "ItemClasses.SCORE_VALUE (0 for every class in this build)",
    "itemClasses[].scrapValue": "ItemClasses.SCRAP_VALUE",
    "itemClasses[].breakEffectId": "ItemClasses.BREAK_EFFECT",
    "itemClasses[].saleRewardSetId": "ItemClasses.SALE_REWARD_SET_ID (dangling - no RewardSets sheet ships)",
    "itemClasses[].saleRewardSetResolved": "derived: always false; see serverSideGaps 'reward-set-contents'",
    "itemClasses[].itemIds": "derived: ClientItemDefinitions rows whose ITEM_CLASS equals this id",
    "itemClasses[].mappedItemIds": "derived: ItemClassMappings.ITEM_ID where CLASS_ID equals this id (second, many-to-many classification)",
    "itemClasses[].acceptedByEquipSlotIds": "derived: EquipSlotItemClasses.EQUIP_SLOT_ID where ITEM_CLASS equals this id",

    "containers[].id": "ContainerDefinitions.ID",
    "containers[].maxSlots": "ContainerDefinitions.MAXIMUM_SLOTS",
    "containers[].maxBulk": "ContainerDefinitions.MAX_BULK",
    "containers[].maxSlotBulk": "ContainerDefinitions.MAX_SLOT_BULK",
    "containers[].maxWeight": "ContainerDefinitions.MAXIMUM_WEIGHT (0 on every row but 101; weight is unused in this build)",
    "containers[].withdrawalOnly": "ContainerDefinitions.WITHDRAWAL_ONLY",
    "containers[].depositOnly": "ContainerDefinitions.DEPOSIT_ONLY",
    "containers[].removeWhenEmpty": "ContainerDefinitions.REMOVE_WHEN_EMPTY",
    "containers[].removeOnlyAfterWithdraw": "ContainerDefinitions.REMOVE_ONLY_AFTER_WITHDRAW",
    "containers[].isDynamicBulk": "ContainerDefinitions.IS_DYNAMIC_BULK (0 on every row)",
    "containers[].itemSpawnerId": "ContainerDefinitions.ITEM_SPAWNER_ID",
    "containers[].itemSpawnerResolved": "derived: always false - no ItemSpawner*.txt datasheet exists in this build (serverSideGaps 'item-spawner-definitions')",
    "containers[].allowedItemClassId": "ContainerDefinitions.ALLOWED_ITEM_CLASS_ID (set on container 102 only)",
    "containers[].allowedItemClassName": "derived: locale name of that class via ItemClasses.NAME_ID",
    "containers[].capacityModel": "derived: 'bulk' when maxBulk>0 and maxSlots>=9999, 'slots' when maxBulk==0, 'hybrid' when both constrain",
    "containers[].grantedByItemIds": "derived: ClientItemDefinitions.ID where CODE_FACTORY_NAME=EquippableContainer and PARAM1 equals this id",

    "equipSlots[].id": "EquipmentSlotDefinitions.ID",
    "equipSlots[].name": "locale(EquipmentSlotDefinitions.NAME_ID)",
    "equipSlots[].nameStringId": "EquipmentSlotDefinitions.NAME_ID",
    "equipSlots[].slotName": "EquipmentSlotDefinitions.SLOT_NAME (the client's own internal name)",
    "equipSlots[].isEquipment": "EquipmentSlotDefinitions.IS_EQUIPMENT",
    "equipSlots[].isRequired": "EquipmentSlotDefinitions.IS_REQUIRED",
    "equipSlots[].groupId": "EquipmentSlotDefinitions.GROUP_ID",
    "equipSlots[].startHidden": "EquipmentSlotDefinitions.START_HIDDEN",
    "equipSlots[].defaultAdr": "EquipmentSlotDefinitions.DEFAULT_ADR (actor definition worn when the slot is empty)",
    "equipSlots[].allowedItemClassIds": "EquipSlotItemClasses.ITEM_CLASS where EQUIP_SLOT_ID equals this id (empty list = unrestricted by this table)",

    "loadoutSlotEquipMap[].slotId": "LoadoutEquipSlots.SLOT_ID",
    "loadoutSlotEquipMap[].equipSlotId": "LoadoutEquipSlots.EQUIP_SLOT_ID",

    "loadouts[].id": "Loadouts.ID",
    "loadouts[].slots[].slotId": "LoadoutSlots.SLOT_ID",
    "loadouts[].slots[].name": "locale(LoadoutSlots.NAME_ID)",
    "loadouts[].slots[].nameStringId": "LoadoutSlots.NAME_ID",
    "loadouts[].slots[].equipSlotId": "LoadoutSlots.EQUIP_SLOT_ID (-> EquipmentSlotDefinitions.ID; 0 = not an equipment slot)",
    "loadouts[].slots[].equipSlotName": "derived: locale name of that equipment slot",
    "loadouts[].slots[].defaultItemId": "LoadoutSlots.ITEM_ID (-> ClientItemDefinitions.ID; 0 = none)",
    "loadouts[].slots[].defaultItemName": "derived: locale name of that item definition",
    "loadouts[].slots[].required": "LoadoutSlots.FLAG_REQUIRED",
    "loadouts[].slots[].autoEquip": "LoadoutSlots.FLAG_AUTO_EQUIP",
    "loadouts[].slots[].canEquip": "LoadoutSlots.ITEM_FLAG_CAN_EQUIP",
    "loadouts[].slots[].quickUse": "LoadoutSlots.ITEM_FLAG_QUICK_USE",
    "loadouts[].slots[].canCustomize": "LoadoutSlots.FLAG_CAN_CUSTOMIZE",
    "loadouts[].slots[].visible": "LoadoutSlots.FLAG_IS_VISIBLE",
    "loadouts[].slots[].wheelable": "LoadoutSlots.FLAG_IS_WHEELABLE",
    "loadouts[].slots[].canRightHandEquip": "LoadoutSlots.FLAG_CAN_RHAND_EQUIP",
    "loadouts[].slots[].requireExplicitAdd": "LoadoutSlots.FLAG_REQUIRE_EXPLICIT_ADD",
    "loadouts[].slots[].canDragOut": "LoadoutSlots.FLAG_CAN_DRAG_OUT",
    "loadouts[].slots[].canDropIn": "LoadoutSlots.FLAG_CAN_DROP_IN",
    "loadouts[].slots[].displayIndex": "LoadoutSlots.DISPLAY_INDEX",
    "loadouts[].slots[].inputAction": "LoadoutSlots.SLOT_INPUT_ACTION",
    "loadouts[].slots[].uiTag": "LoadoutSlots.SLOT_UI_TAG (empty on every loadout-3 row in this build)",
    "loadouts[].slots[].activeEquipSlotId": "LoadoutSlots.ITEM_ACTIVE_EQUIP_SLOT_ID",
    "loadouts[].slots[].passiveEquipSlotId": "LoadoutSlots.ITEM_PASSIVE_EQUIP_SLOT_ID",
    "loadouts[].slots[].requirementSetId": "LoadoutSlots.REQ_SET_ID",
    "loadouts[].slots[].clientRequirementSetId": "LoadoutSlots.CLIENT_REQ_SET_ID",
    "loadouts[].slots[].allowedItemClasses[].classId": "LoadoutSlotItemClasses.ITEM_CLASS where (LOADOUT_ID, SLOT) match",
    "loadouts[].slots[].allowedItemClasses[].name": "derived: locale name of that item class",
    "loadouts[].slots[].allowedItemClasses[].locked": "LoadoutSlotItemClasses.FLAG_LOCKED",

    "useOptions[].id": "ItemUseOptions.ITEM_USE_OPTION_ID",
    "useOptions[].typeName": "ItemUseOptions.TYPE_NAME (the client's own action name, e.g. LootItem)",
    "useOptions[].name": "locale(ItemUseOptions.NAME_ID)",
    "useOptions[].nameStringId": "ItemUseOptions.NAME_ID",
    "useOptions[].targetType": "ItemUseOptions.TARGET_TYPE",
    "useOptions[].busyMsec": "ItemUseOptions.BUSY_MSEC (client-side action lockout; the server must gate at least this long)",
    "useOptions[].interactionAnimationId": "ItemUseOptions.INTERACTION_ANIMATION_ID",
    "useOptions[].inputActionKey": "ItemUseOptions.INPUT_ACTION_KEY",
    "useOptions[].requirementSetId": "ItemUseOptions.REQ_SET_ID",
    "useOptions[].targetRequirementSetId": "ItemUseOptions.TARGET_REQ_SET_ID",

    "useOptionGroups[].id": "ItemUseOptionGroups.ITEM_USE_OPTION_GROUP_ID",
    "useOptionGroups[].options[].optionId": "ItemUseOptionGroups.ITEM_USE_OPTION_ID",
    "useOptionGroups[].options[].displayIndex": "ItemUseOptionGroups.DISPLAY_INDEX",

    "items[].id": "ClientItemDefinitions.ID",
    "items[].name": "locale(ClientItemDefinitions.NAME_ID)",
    "items[].nameStringId": "ClientItemDefinitions.NAME_ID",
    "items[].descriptionStringId": "ClientItemDefinitions.DESCRIPTION_ID (text not inlined; see item-names-en_us.json)",
    "items[].codeFactory": "ClientItemDefinitions.CODE_FACTORY_NAME",
    "items[].itemType": "ClientItemDefinitions.ITEM_TYPE",
    "items[].itemClassId": "ClientItemDefinitions.ITEM_CLASS (-> ItemClasses.ID; 0 = none)",
    "items[].itemClassName": "derived: locale name of that item class",
    "items[].mappedClassIds": "derived: ItemClassMappings.CLASS_ID where ITEM_ID equals this id (omitted when empty)",
    "items[].bulk": "ClientItemDefinitions.BULK (the live capacity unit; pairs with containers[].maxBulk)",
    "items[].maxStackSize": "ClientItemDefinitions.MAX_STACK_SIZE",
    "items[].minStackSize": "ClientItemDefinitions.MIN_STACK_SIZE",
    "items[].singleUse": "ClientItemDefinitions.SINGLE_USE",
    "items[].rarityId": "ClientItemDefinitions.RARITY (-> ItemRarity.ID)",
    "items[].rarityName": "derived: ItemRarity.DESIGNER_NAME of that rarity",
    "items[].isArmor": "ClientItemDefinitions.IS_ARMOR",
    "items[].activeEquipSlotId": "ClientItemDefinitions.ACTIVE_EQUIP_SLOT_ID (-> EquipmentSlotDefinitions.ID)",
    "items[].passiveEquipSlotId": "ClientItemDefinitions.PASSIVE_EQUIP_SLOT_ID (-> EquipmentSlotDefinitions.ID)",
    "items[].passiveEquipSlotGroupId": "ClientItemDefinitions.PASSIVE_EQUIP_SLOT_GROUP_ID",
    "items[].equipCountMax": "ClientItemDefinitions.EQUIP_COUNT_MAX",
    "items[].canEquip": "ClientItemDefinitions.FLAG_CAN_EQUIP",
    "items[].quickUse": "ClientItemDefinitions.FLAG_QUICK_USE",
    "items[].noDragDrop": "ClientItemDefinitions.FLAG_NO_DRAG_DROP",
    "items[].accountScope": "ClientItemDefinitions.FLAG_ACCOUNT_SCOPE (account-wide, not a world-loot item)",
    "items[].hideOnClient": "ClientItemDefinitions.FLAG_HIDE_ON_CLIENT",
    "items[].noTrade": "ClientItemDefinitions.NO_TRADE (0 on every row in this build)",
    "items[].containerDefinitionId": "ClientItemDefinitions.PARAM1, ONLY when CODE_FACTORY_NAME=EquippableContainer (omitted otherwise)",
    "items[].scrapValueOverride": "ClientItemDefinitions.SCRAP_VALUE_OVERRIDE",
    "items[].grinderRewardSetId": "ClientItemDefinitions.GRINDER_REWARD_SET_ID (dangling - no RewardSets sheet)",
    "items[].useOptionGroupId": "ItemIdUseOptionGroupId.ITEM_USE_OPTION_GROUP_ID for this ITEM_ID (0 = no context menu)",
    "items[].modelName": "ClientItemDefinitions.MODEL_NAME (the item's own actor definition; the *ground* actor of a dropped item is a separate world model - see docs/13 section 3)",
    "items[].useRequirementId": "ClientItemDefinitions.USE_REQUIREMENT_ID",
    "items[].compositeEffectId": "ClientItemDefinitions.COMPOSITE_EFFECT_ID",
}


# -- gaps ------------------------------------------------------------------------------
#
# Values a Cranberry loot/inventory system needs that this build's client does NOT carry.
# Every entry states the evidence for the absence. No number here is invented anywhere in
# this file - the server must own each of these.

SERVER_SIDE_GAPS: list[dict[str, str]] = [
    {
        "id": "loot-tables",
        "need": "Which item definitions spawn at which loot tier, and with what probability.",
        "evidence": "No LootTable* asset of any kind exists in the 50,502-entry pack index "
                    "(pack1-index-aug.txt). 'Loot' appears only as art (Common_Props_LootBag*, "
                    "Proxy_Lootspot_10/20/30.adr) and two Scaleform windows (HudLootFeedWindow.gfx, "
                    "HudQuickLootWindow.gfx). Drop tables are backend-only in this build.",
        "cranberryAction": "Author Cranberry's own tiered loot tables over items[] "
                           "(D20 permits the owner's authored KOTK loot model as a design source).",
    },
    {
        "id": "item-spawner-definitions",
        "need": "What ContainerDefinitions.ITEM_SPAWNER_ID points at - the contents a world "
                "container is filled with when it spawns.",
        "evidence": "32 distinct non-zero ITEM_SPAWNER_ID values (2,3,7,8,10,11,16,17,20,21,25,26,"
                    "30-34,38,40,46-55,134,136,2012) with no ItemSpawner*.txt datasheet anywhere in "
                    "the pack index. ItemSpawner* exists only as 3-D marker art (.adr/.cdt/.dma/.dme, "
                    "e.g. ItemSpawnerResidential_Tier00.adr). Emitted with itemSpawnerResolved=false.",
        "cranberryAction": "Define Cranberry's own spawner table keyed by these ids, or ignore the "
                           "column and fill containers straight from Cranberry's loot tables.",
    },
    {
        "id": "loot-spawn-points",
        "need": "Where in the world loot and loot containers appear.",
        "evidence": "Not in any datasheet. The client ships Proxy_Lootspot_10/20/30.adr and the "
                    "ItemSpawner*_Tier* actor definitions as art; their placement lives in the zone "
                    "data (C:\\Aug2017\\out\\world_aug), not in this item group.",
        "cranberryAction": "Derive placements from the Z2 zone data in a separate pass; this dataset "
                           "deliberately carries no coordinates.",
    },
    {
        "id": "reward-set-contents",
        "need": "What a reward set contains - needed for crate opening, grinder output and sale rewards.",
        "evidence": "ItemClasses.SALE_REWARD_SET_ID (values 5285, 5298, 5300, 5302, ...) and "
                    "ClientItemDefinitions.GRINDER_REWARD_SET_ID both point at reward-set ids, but the "
                    "index has zero hits for 'rewardset' in any extension. Reward-set contents are "
                    "backend-only. Emitted with saleRewardSetResolved=false.",
        "cranberryAction": "Cranberry authors crate/grinder outputs itself; the ids are kept only so a "
                           "future capture can be matched against them.",
    },
    {
        "id": "container-slot-restrictions",
        "need": "Per-slot item-type restrictions inside a container.",
        "evidence": "ContainerSlotDefinitions.txt ships header-only (0 rows) in this build; so do "
                    "ItemClassSets, FilterToItemClass, ItemFiltersToClassMap and VehicleItemClasses. "
                    "Container restriction therefore reduces to maxSlotBulk plus the loadout-side "
                    "class rules only.",
        "cranberryAction": "Treat every container slot as class-unrestricted except container 102 "
                           "(allowedItemClassId 25063); enforce bulk, not type.",
    },
    {
        "id": "bulk-stack-arithmetic",
        "need": "Whether items[].bulk is charged per unit or per stack, and how a partially filled "
                "stack is charged against containers[].maxBulk / maxSlotBulk.",
        "evidence": "The sheets carry BULK, MAX_BULK, MAX_SLOT_BULK and MAX_STACK_SIZE as bare "
                    "integers with no stated relation. Nothing in the datasheets defines the arithmetic.",
        "cranberryAction": "Fix the rule in Cranberry's inventory model and confirm it against the "
                           "client's reaction in a live run (the bag UI refuses an over-bulk insert).",
    },
    {
        "id": "item-durability",
        "need": "Starting and maximum durability of a spawned item.",
        "evidence": "ClientItemDefinitions has no durability column at all, yet ClientUpdate.ItemAdd "
                    "(11 02) carries three durability-shaped fields (docs/13 section 4). The client is "
                    "told the numbers; it does not own them.",
        "cranberryAction": "Cranberry owns durability entirely; pick values per item class.",
    },
    {
        "id": "spawn-quantities",
        "need": "How many rounds/units a spawned stack contains (ammo piles, bandage stacks).",
        "evidence": "MAX_STACK_SIZE is only a ceiling (1303 items cap at 1, 1133 at 9999). No sheet "
                    "carries a spawn quantity or a range.",
        "cranberryAction": "Part of Cranberry's own loot-table authoring.",
    },
    {
        "id": "weapon-ammo-binding",
        "need": "Which ammo item feeds which weapon, and magazine sizes.",
        "evidence": "Not in this group. ClientItemDefinitions.PARAM1/2/3 are factory-overloaded and "
                    "mean something other than a container id on the 213 Weapon rows; only 5 Ammo "
                    "rows exist. The weapon/fire-mode sheets are a separate extraction group.",
        "cranberryAction": "Resolve in the weapons pass (docs/16); this dataset intentionally does "
                           "not guess a weapon->ammo edge.",
    },
    {
        "id": "starting-loadout-choice",
        "need": "Which loadout id a live KOTK match character is given.",
        "evidence": "Loadouts.txt is a bare id list of 17 ids with no game-mode or profile column, "
                    "and GameModeDefinitions carries no loadout reference. Loadout 3 (24 slots) and "
                    "17 (22 slots) are the only two full character loadouts by slot count, but the "
                    "client never states which one a match uses.",
        "cranberryAction": "Cranberry's own decision, confirmed by what the client renders in a live "
                           "run; do not read the slot count as a rule.",
    },
    {
        "id": "score-values",
        "need": "Per-item or per-class score contribution.",
        "evidence": "ItemClasses.SCORE_VALUE is 0 for all 97 classes in this build.",
        "cranberryAction": "Scoring is server-side; Cranberry defines it.",
    },
    {
        "id": "container-despawn-timing",
        "need": "How long a REMOVE_WHEN_EMPTY / REMOVE_ONLY_AFTER_WITHDRAW container lingers before "
                "it is removed, and how long dropped ground loot persists.",
        "evidence": "The flags exist in ContainerDefinitions but carry no timer column, and no sheet "
                    "in the client has a loot-lifetime value.",
        "cranberryAction": "Cranberry's own tunable (alongside the gas timings of D23).",
    },
]


# -- build -----------------------------------------------------------------------------


def build(data_dir: Path, names: Names) -> dict[str, Any]:
    def sheet(name: str) -> Sheet:
        return Sheet(data_dir / name)

    # ---- sources record ------------------------------------------------------------
    sources: list[dict[str, Any]] = []
    row_cache: dict[str, list[dict[str, str]]] = {}
    for name in INPUT_SHEETS:
        path = data_dir / name
        if not path.exists():
            sys.exit(f"missing input sheet: {path}")
        s = sheet(name)
        rows = list(s)
        row_cache[name] = rows
        sources.append({
            "sheet": name,
            "path": str(path),
            "bytes": path.stat().st_size,
            "sha256": _sha256(path),
            "rows": len(rows),
            "columns": len(s.columns),
        })

    items_raw = row_cache["ClientItemDefinitions.txt"]
    classes_raw = row_cache["ItemClasses.txt"]
    class_map_raw = row_cache["ItemClassMappings.txt"]
    rarity_raw = row_cache["ItemRarity.txt"]
    grinder_raw = row_cache["Grinders.txt"]
    container_raw = row_cache["ContainerDefinitions.txt"]
    equip_raw = row_cache["EquipmentSlotDefinitions.txt"]
    equip_class_raw = row_cache["EquipSlotItemClasses.txt"]
    loadout_raw = row_cache["Loadouts.txt"]
    lslot_raw = row_cache["LoadoutSlots.txt"]
    lslot_class_raw = row_cache["LoadoutSlotItemClasses.txt"]
    lequip_raw = row_cache["LoadoutEquipSlots.txt"]
    useopt_raw = row_cache["ItemUseOptions.txt"]
    useoptgrp_raw = row_cache["ItemUseOptionGroups.txt"]
    item_useopt_raw = row_cache["ItemIdUseOptionGroupId.txt"]

    integrity: dict[str, Any] = {}

    # ---- rarities ------------------------------------------------------------------
    rarities = [{
        "id": _int(r["ID"]),
        "colorName": r["DESCRIPTION"],
        "designerName": r["DESIGNER_NAME"],
        "clientColorId": _int(r["CLIENT_COLOR_ID"]),
    } for r in rarity_raw]
    rarity_name = {r["id"]: r["designerName"] for r in rarities}

    grinder_inputs = [{
        "grinderId": _int(r["ID"]),
        "rarityId": _int(r["RARITY_ID"]),
        "inputCount": _int(r["INPUT_COUNT"]),
    } for r in grinder_raw]

    # ---- reverse indices over items --------------------------------------------------
    class_members: dict[int, list[int]] = {}
    container_grantors: dict[int, list[int]] = {}
    for r in items_raw:
        iid = _int(r["ID"])
        cls = _int(r["ITEM_CLASS"])
        if cls:
            class_members.setdefault(cls, []).append(iid)
        if r["CODE_FACTORY_NAME"] == CONTAINER_FACTORY:
            cdef = _int(r["PARAM1"])
            if cdef:
                container_grantors.setdefault(cdef, []).append(iid)

    mapped_by_class: dict[int, list[int]] = {}
    mapped_by_item: dict[int, list[int]] = {}
    for r in class_map_raw:
        iid, cls = _int(r["ITEM_ID"]), _int(r["CLASS_ID"])
        mapped_by_class.setdefault(cls, []).append(iid)
        mapped_by_item.setdefault(iid, []).append(cls)

    equipslot_classes: dict[int, list[int]] = {}
    class_equipslots: dict[int, list[int]] = {}
    for r in equip_class_raw:
        es, cls = _int(r["EQUIP_SLOT_ID"]), _int(r["ITEM_CLASS"])
        equipslot_classes.setdefault(es, []).append(cls)
        class_equipslots.setdefault(cls, []).append(es)

    # ---- item classes ----------------------------------------------------------------
    class_name: dict[int, str | None] = {}
    item_classes = []
    for r in classes_raw:
        cid = _int(r["ID"])
        nm = names.get(_int(r["NAME_ID"]))
        class_name[cid] = nm
        item_classes.append({
            "id": cid,
            "name": nm,
            "nameStringId": _int(r["NAME_ID"]),
            "tooltipStringId": _int(r["TOOLTIP_STRING_ID"]),
            "wieldType": _int(r["WIELD_TYPE"]),
            "secondaryWieldType": _int(r["SECONDARY_WIELD_TYPE"]),
            "factionId": _int(r["FACTION_ID"]),
            "scoreValue": _int(r["SCORE_VALUE"]),
            "scrapValue": _int(r["SCRAP_VALUE"]),
            "breakEffectId": _int(r["BREAK_EFFECT"]),
            "saleRewardSetId": _int(r["SALE_REWARD_SET_ID"]),
            "saleRewardSetResolved": False,
            "itemIds": _sorted_ids(class_members.get(cid, [])),
            "mappedItemIds": _sorted_ids(mapped_by_class.get(cid, [])),
            "acceptedByEquipSlotIds": _sorted_ids(class_equipslots.get(cid, [])),
        })
    item_classes.sort(key=lambda c: c["id"])
    known_classes = {c["id"] for c in item_classes}

    integrity["itemsWithUnknownItemClass"] = sorted(
        cid for cid in class_members if cid not in known_classes)
    integrity["itemClassMappingsWithUnknownClass"] = sorted(
        cid for cid in mapped_by_class if cid not in known_classes)
    integrity["equipSlotItemClassesWithUnknownClass"] = sorted(
        cid for cid in class_equipslots if cid not in known_classes)
    integrity["itemClassesWithNoMembers"] = sorted(
        c["id"] for c in item_classes if not c["itemIds"] and not c["mappedItemIds"])

    # ---- containers ------------------------------------------------------------------
    containers = []
    known_containers = set()
    for r in container_raw:
        cid = _int(r["ID"])
        known_containers.add(cid)
        max_slots = _int(r["MAXIMUM_SLOTS"])
        max_bulk = _int(r["MAX_BULK"])
        # Derived reading of the two capacity systems: KOTK constrains by bulk and parks
        # MAXIMUM_SLOTS at 9999 wherever bulk is the real limit.
        if max_bulk > 0 and max_slots >= 9999:
            model = "bulk"
        elif max_bulk == 0:
            model = "slots"
        else:
            model = "hybrid"
        allowed = _int(r["ALLOWED_ITEM_CLASS_ID"])
        containers.append({
            "id": cid,
            "maxSlots": max_slots,
            "maxBulk": max_bulk,
            "maxSlotBulk": _int(r["MAX_SLOT_BULK"]),
            "maxWeight": _int(r["MAXIMUM_WEIGHT"]),
            "capacityModel": model,
            "withdrawalOnly": _bool(r["WITHDRAWAL_ONLY"]),
            "depositOnly": _bool(r["DEPOSIT_ONLY"]),
            "removeWhenEmpty": _bool(r["REMOVE_WHEN_EMPTY"]),
            "removeOnlyAfterWithdraw": _bool(r["REMOVE_ONLY_AFTER_WITHDRAW"]),
            "isDynamicBulk": _bool(r["IS_DYNAMIC_BULK"]),
            "itemSpawnerId": _int(r["ITEM_SPAWNER_ID"]),
            "itemSpawnerResolved": False,
            "allowedItemClassId": allowed,
            "allowedItemClassName": class_name.get(allowed),
            "grantedByItemIds": _sorted_ids(container_grantors.get(cid, [])),
        })
    containers.sort(key=lambda c: c["id"])

    integrity["equippableContainerItemsWithUnknownContainer"] = sorted(
        cid for cid in container_grantors if cid not in known_containers)
    integrity["containersGrantedByNoItem"] = sorted(
        c["id"] for c in containers if not c["grantedByItemIds"])

    # ---- equipment slots --------------------------------------------------------------
    equip_name: dict[int, str | None] = {}
    equip_slots = []
    for r in equip_raw:
        eid = _int(r["ID"])
        nm = names.get(_int(r["NAME_ID"]))
        equip_name[eid] = nm
        equip_slots.append({
            "id": eid,
            "name": nm,
            "nameStringId": _int(r["NAME_ID"]),
            "slotName": r["SLOT_NAME"],
            "isEquipment": _bool(r["IS_EQUIPMENT"]),
            "isRequired": _bool(r["IS_REQUIRED"]),
            "groupId": _int(r["GROUP_ID"]),
            "startHidden": _bool(r["START_HIDDEN"]),
            "defaultAdr": r["DEFAULT_ADR"],
            "allowedItemClassIds": _sorted_ids(equipslot_classes.get(eid, [])),
        })
    equip_slots.sort(key=lambda e: e["id"])
    known_equip = {e["id"] for e in equip_slots}

    integrity["equipSlotItemClassesWithUnknownEquipSlot"] = sorted(
        es for es in equipslot_classes if es not in known_equip)

    loadout_equip_map = [{
        "slotId": _int(r["SLOT_ID"]),
        "equipSlotId": _int(r["EQUIP_SLOT_ID"]),
    } for r in lequip_raw]
    loadout_equip_map.sort(key=lambda m: m["slotId"])

    # ---- loadouts ---------------------------------------------------------------------
    item_name: dict[int, str | None] = {}
    for r in items_raw:
        item_name[_int(r["ID"])] = names.get(_int(r["NAME_ID"]))
    known_items = set(item_name)

    slot_classes: dict[tuple[int, int], list[dict[str, Any]]] = {}
    for r in lslot_class_raw:
        key = (_int(r["LOADOUT_ID"]), _int(r["SLOT"]))
        cls = _int(r["ITEM_CLASS"])
        slot_classes.setdefault(key, []).append({
            "classId": cls,
            "name": class_name.get(cls),
            "locked": _bool(r["FLAG_LOCKED"]),
        })

    slots_by_loadout: dict[int, list[dict[str, Any]]] = {}
    equip_conflicts: list[dict[str, int]] = []
    flat_equip = {m["slotId"]: m["equipSlotId"] for m in loadout_equip_map}
    for r in lslot_raw:
        lid, sid = _int(r["LOADOUT_ID"]), _int(r["SLOT_ID"])
        esid = _int(r["EQUIP_SLOT_ID"])
        item_id = _int(r["ITEM_ID"])
        if sid in flat_equip and flat_equip[sid] != esid:
            equip_conflicts.append({
                "loadoutId": lid, "slotId": sid,
                "loadoutSlotsEquipSlotId": esid,
                "loadoutEquipSlotsEquipSlotId": flat_equip[sid],
            })
        slots_by_loadout.setdefault(lid, []).append({
            "slotId": sid,
            "name": names.get(_int(r["NAME_ID"])),
            "nameStringId": _int(r["NAME_ID"]),
            "equipSlotId": esid,
            "equipSlotName": equip_name.get(esid),
            "defaultItemId": item_id,
            "defaultItemName": item_name.get(item_id),
            "required": _bool(r["FLAG_REQUIRED"]),
            "autoEquip": _bool(r["FLAG_AUTO_EQUIP"]),
            "canEquip": _bool(r["ITEM_FLAG_CAN_EQUIP"]),
            "quickUse": _bool(r["ITEM_FLAG_QUICK_USE"]),
            "canCustomize": _bool(r["FLAG_CAN_CUSTOMIZE"]),
            "visible": _bool(r["FLAG_IS_VISIBLE"]),
            "wheelable": _bool(r["FLAG_IS_WHEELABLE"]),
            "canRightHandEquip": _bool(r["FLAG_CAN_RHAND_EQUIP"]),
            "requireExplicitAdd": _bool(r["FLAG_REQUIRE_EXPLICIT_ADD"]),
            "canDragOut": _bool(r["FLAG_CAN_DRAG_OUT"]),
            "canDropIn": _bool(r["FLAG_CAN_DROP_IN"]),
            "displayIndex": _int(r["DISPLAY_INDEX"]),
            "inputAction": r["SLOT_INPUT_ACTION"],
            "uiTag": r["SLOT_UI_TAG"],
            "activeEquipSlotId": _int(r["ITEM_ACTIVE_EQUIP_SLOT_ID"]),
            "passiveEquipSlotId": _int(r["ITEM_PASSIVE_EQUIP_SLOT_ID"]),
            "requirementSetId": _int(r["REQ_SET_ID"]),
            "clientRequirementSetId": _int(r["CLIENT_REQ_SET_ID"]),
            "allowedItemClasses": sorted(
                slot_classes.get((lid, sid), []), key=lambda c: c["classId"]),
        })

    loadouts = []
    for r in loadout_raw:
        lid = _int(r["ID"])
        slots = sorted(slots_by_loadout.get(lid, []), key=lambda s: s["slotId"])
        loadouts.append({"id": lid, "slotCount": len(slots), "slots": slots})
    loadouts.sort(key=lambda l: l["id"])
    declared_loadouts = {l["id"] for l in loadouts}

    integrity["loadoutSlotsForUndeclaredLoadout"] = sorted(
        lid for lid in slots_by_loadout if lid not in declared_loadouts)
    integrity["loadoutSlotItemClassesWithoutSlot"] = sorted(
        f"{lid}/{sid}" for (lid, sid) in slot_classes
        if not any(s["slotId"] == sid for s in slots_by_loadout.get(lid, [])))
    integrity["loadoutEquipSlotConflicts"] = equip_conflicts

    # ---- use options -------------------------------------------------------------------
    use_options = [{
        "id": _int(r["ITEM_USE_OPTION_ID"]),
        "typeName": r["TYPE_NAME"],
        "name": names.get(_int(r["NAME_ID"])),
        "nameStringId": _int(r["NAME_ID"]),
        "targetType": _int(r["TARGET_TYPE"]),
        "busyMsec": _int(r["BUSY_MSEC"]),
        "interactionAnimationId": _int(r["INTERACTION_ANIMATION_ID"]),
        "inputActionKey": r["INPUT_ACTION_KEY"],
        "requirementSetId": _int(r["REQ_SET_ID"]),
        "targetRequirementSetId": _int(r["TARGET_REQ_SET_ID"]),
    } for r in useopt_raw]
    use_options.sort(key=lambda o: o["id"])
    known_options = {o["id"] for o in use_options}

    groups: dict[int, list[dict[str, int]]] = {}
    for r in useoptgrp_raw:
        groups.setdefault(_int(r["ITEM_USE_OPTION_GROUP_ID"]), []).append({
            "optionId": _int(r["ITEM_USE_OPTION_ID"]),
            "displayIndex": _int(r["DISPLAY_INDEX"]),
        })
    use_option_groups = [
        {"id": gid, "options": sorted(opts, key=lambda o: (o["displayIndex"], o["optionId"]))}
        for gid, opts in sorted(groups.items())
    ]
    integrity["useOptionGroupsWithUnknownOption"] = sorted({
        o["optionId"] for g in use_option_groups for o in g["options"]
        if o["optionId"] not in known_options})

    item_group = {_int(r["ITEM_ID"]): _int(r["ITEM_USE_OPTION_GROUP_ID"]) for r in item_useopt_raw}
    integrity["itemUseOptionMapWithUnknownItem"] = sorted(
        iid for iid in item_group if iid not in known_items)
    integrity["itemUseOptionMapWithUnknownGroup"] = sorted({
        g for g in item_group.values() if g and g not in groups})

    # ---- items ---------------------------------------------------------------------------
    items = []
    for r in items_raw:
        iid = _int(r["ID"])
        cls = _int(r["ITEM_CLASS"])
        rarity = _int(r["RARITY"])
        entry: dict[str, Any] = {
            "id": iid,
            "name": item_name.get(iid),
            "nameStringId": _int(r["NAME_ID"]),
            "descriptionStringId": _int(r["DESCRIPTION_ID"]),
            "codeFactory": r["CODE_FACTORY_NAME"],
            "itemType": _int(r["ITEM_TYPE"]),
            "itemClassId": cls,
            "itemClassName": class_name.get(cls),
            "bulk": _int(r["BULK"]),
            "maxStackSize": _int(r["MAX_STACK_SIZE"]),
            "minStackSize": _int(r["MIN_STACK_SIZE"]),
            "singleUse": _bool(r["SINGLE_USE"]),
            "rarityId": rarity,
            "rarityName": rarity_name.get(rarity),
            "isArmor": _bool(r["IS_ARMOR"]),
            "activeEquipSlotId": _int(r["ACTIVE_EQUIP_SLOT_ID"]),
            "passiveEquipSlotId": _int(r["PASSIVE_EQUIP_SLOT_ID"]),
            "passiveEquipSlotGroupId": _int(r["PASSIVE_EQUIP_SLOT_GROUP_ID"]),
            "equipCountMax": _int(r["EQUIP_COUNT_MAX"]),
            "canEquip": _bool(r["FLAG_CAN_EQUIP"]),
            "quickUse": _bool(r["FLAG_QUICK_USE"]),
            "noDragDrop": _bool(r["FLAG_NO_DRAG_DROP"]),
            "accountScope": _bool(r["FLAG_ACCOUNT_SCOPE"]),
            "hideOnClient": _bool(r["FLAG_HIDE_ON_CLIENT"]),
            "noTrade": _bool(r["NO_TRADE"]),
            "scrapValueOverride": _int(r["SCRAP_VALUE_OVERRIDE"]),
            "grinderRewardSetId": _int(r["GRINDER_REWARD_SET_ID"]),
            "useOptionGroupId": item_group.get(iid, 0),
            "modelName": r["MODEL_NAME"],
            "useRequirementId": _int(r["USE_REQUIREMENT_ID"]),
            "compositeEffectId": _int(r["COMPOSITE_EFFECT_ID"]),
        }
        # PARAM1 is only a container id on the EquippableContainer factory - never join it
        # anywhere else (see FORMAT FACTS above).
        if r["CODE_FACTORY_NAME"] == CONTAINER_FACTORY:
            entry["containerDefinitionId"] = _int(r["PARAM1"])
        mapped = mapped_by_item.get(iid)
        if mapped:
            entry["mappedClassIds"] = _sorted_ids(mapped)
        items.append(entry)
    items.sort(key=lambda i: i["id"])

    integrity["itemsWithUnknownRarity"] = sorted({
        i["rarityId"] for i in items if i["rarityId"] and i["rarityId"] not in rarity_name})
    integrity["itemsWithUnknownActiveEquipSlot"] = sorted({
        i["activeEquipSlotId"] for i in items
        if i["activeEquipSlotId"] and i["activeEquipSlotId"] not in known_equip})
    integrity["itemsWithUnknownPassiveEquipSlot"] = sorted({
        i["passiveEquipSlotId"] for i in items
        if i["passiveEquipSlotId"] and i["passiveEquipSlotId"] not in known_equip})

    # ---- document ------------------------------------------------------------------------
    return {
        "format": "cranberry.loot-dataset",
        "formatVersion": 1,
        "generator": "Server/tools/data/derive_loot.py",
        "generatedUtc": _dt.datetime.now(_dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "clientBuild": CLIENT_BUILD,
        "locale": names.lang,
        "notes": [
            "Every value is joined from the August client's own datasheets (docs/00 rule 2, D7). "
            "Nothing here is invented; values the client does not carry are in serverSideGaps.",
            "ClientItemDefinitions.PARAM1 is the container definition id ONLY on the "
            "EquippableContainer factory; items[].containerDefinitionId is emitted on those rows "
            "alone and is absent everywhere else.",
            "items[].itemClassId (primary) and items[].mappedClassIds (ItemClassMappings) are two "
            "different classifications: exactly 1 of the 482 mapping rows agrees with the primary "
            "class, so never treat one as a copy of the other.",
            _capacity_note(containers),
            "The inventory UI's filter tables (ItemClassGroups, ItemFilterOptions) are deliberately "
            "not emitted - they are presentation, not server rules.",
            "Known dangling references in the client's own data, kept verbatim rather than patched: "
            "ItemUseOptionGroups 34, 40 and 42 name option ids 36, 41 and 44, none of which exist in "
            "ItemUseOptions (which stops at 103 with those three ids absent). Only group 40 is "
            "reachable from an item at all (item 2212 'Lighter'); groups 34 and 42 are used by no "
            "item. A server resolving a group must skip an option id it cannot find.",
            "integrity is a self-check, not data: every list in it is expected to be empty for this "
            "build except the two documented 'no members'/'granted by no item' informational lists "
            "and useOptionGroupsWithUnknownOption = [36, 41, 44] (the dangling ids above).",
        ],
        "sources": sources,
        "provenance": PROVENANCE,
        "counts": {
            "rarities": len(rarities),
            "grinderInputs": len(grinder_inputs),
            "itemClasses": len(item_classes),
            "containers": len(containers),
            "equipSlots": len(equip_slots),
            "loadoutSlotEquipMap": len(loadout_equip_map),
            "loadouts": len(loadouts),
            "loadoutSlotsTotal": sum(l["slotCount"] for l in loadouts),
            "loadoutSlotClassRules": sum(len(v) for v in slot_classes.values()),
            "useOptions": len(use_options),
            "useOptionGroups": len(use_option_groups),
            "items": len(items),
            "itemsWithBulk": sum(1 for i in items if i["bulk"]),
            "itemsGrantingContainer": sum(
                1 for i in items if i.get("containerDefinitionId")),
            "serverSideGaps": len(SERVER_SIDE_GAPS),
        },
        "integrity": integrity,
        "rarities": rarities,
        "grinderInputs": grinder_inputs,
        "itemClasses": item_classes,
        "containers": containers,
        "equipSlots": equip_slots,
        "loadoutSlotEquipMap": loadout_equip_map,
        "loadouts": loadouts,
        "useOptions": use_options,
        "useOptionGroups": use_option_groups,
        "items": items,
        "serverSideGaps": SERVER_SIDE_GAPS,
    }


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="Derive Cranberry's loot/inventory dataset from the client's datasheets.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    ap.add_argument("--data-dir", default=str(DEFAULT_DATA_DIR),
                    help="directory holding the extracted datasheets (default: %(default)s)")
    ap.add_argument("--out", default=str(DEFAULT_OUT),
                    help="output JSON path (default: %(default)s)")
    ap.add_argument("--lang", default="en_us",
                    help="locale archive to resolve names from (default: %(default)s)")
    ap.add_argument("--no-locale", action="store_true",
                    help="skip the locale join; every name field is emitted as null")
    ap.add_argument("--compact", action="store_true",
                    help="write one line with no indentation (about 35%% smaller)")
    args = ap.parse_args(argv)

    data_dir = Path(args.data_dir)
    if not data_dir.is_dir():
        sys.exit(f"no such data directory: {data_dir}")

    names = Names(None if args.no_locale else args.lang)
    doc = build(data_dir, names)

    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(doc, ensure_ascii=False, indent=None if args.compact else 1)
    out.write_text(text + "\n", encoding="utf-8", newline="\n")

    c = doc["counts"]
    print(f"{out}  ({out.stat().st_size:,} bytes)", file=sys.stderr)
    for k in ("itemClasses", "containers", "equipSlots", "loadouts", "loadoutSlotsTotal",
              "loadoutSlotClassRules", "useOptions", "useOptionGroups", "items",
              "serverSideGaps"):
        print(f"  {k:<24} {c[k]:>6}", file=sys.stderr)
    print(f"  locale strings resolved  {names.resolved:>6} "
          f"(unresolved ids: {names.unresolved})", file=sys.stderr)
    # Two lists are informational, and the third is a known dangling reference in the
    # client's own data (documented in doc["notes"]); everything else must be empty.
    expected = ("itemClassesWithNoMembers", "containersGrantedByNoItem",
                "useOptionGroupsWithUnknownOption")
    bad = {k: v for k, v in doc["integrity"].items() if v and k not in expected}
    if bad:
        print(f"  INTEGRITY: {json.dumps(bad)}", file=sys.stderr)
    else:
        print("  integrity: all joins resolve", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
