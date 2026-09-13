#!/usr/bin/env python3
r"""
derive_economy.py - build Cranberry's server-ready ``economy`` dataset from the August
client's own datasheets.

INPUT   C:\Aug2017\out\data_aug\*.txt   (datasheets extracted from the client's
                                         Assets_*.pack with tools/pack/packread.py)
        C:\Aug2017\Client\Locale\en_us_data.dat + .dir   (via tools/locale/localedat.py)
OUTPUT  C:\Aug2017\out\data_aug\derived\economy.json
            one JSON document in this project's own ``cranberry.economy/1`` schema

WHAT THIS COVERS
    The Cranberry "economy" system: currencies (Crowns / Scrap / Skulls / Credits /
    Daybreak Cash), reward crates and their keys, the Scrapyard (scrap payouts + the
    Grinder exchange), the wardrobe (account-item -> wearable-item conversion graph),
    and the data the match-end screen is driven from (rank badges, ranked tiers, panel
    layout, team colours).

    Everything in the output is joined from the client's own sheets and, where a locale
    mapping exists, resolved to English display names. Nothing is invented: every value a
    Cranberry server needs that the client genuinely does not carry is listed in
    ``serverSideGaps`` instead.

SCHEMA (cranberry.economy/1) - top-level keys
    schema, generatedAt, generator, client       provenance header
    provenance                                   field-path -> "Sheet.COLUMN - note"
    currencies[]                                 Currency.txt joined to icons + locale
    rarities[]                                   ItemRarity.txt + observed scrap defaults
    scrapyard{}                                  grinders, scrap values, payout items,
                                                 grinder input families, storefront items
    crates[]                                     RewardCrate / LockedRewardCrate rows
    crateKeys[]                                  RewardCrateKey rows
    bags[]                                       (Account)GiveRewardSet rows
    wardrobe{}                                   conversions, input groups, source labels
    matchEnd{}                                   rank badges, ranked tiers, panel layout,
                                                 team + squad colours, game modes, XP awards
    itemSortTypes[], contentPacks{}, useVerbs[]  supporting UI/behaviour tables
    counts{}                                     row count of every emitted collection
    serverSideGaps[]                             values the server must author itself

FORMAT FACTS RELIED ON (each with its evidence)
  F1  Datasheet dialect (``^`` delimited, ``#`` header, ``*`` key marker, trailing ``^``,
      no quoting/escaping).  Evidence: tools/data/sheet.py, which verified all 36 sheets
      byte-wise.  Two economy sheets deviate and are handled by the same reader:
      InvitationalTeamInfo.txt has no trailing ``^``; TintSemanticTables.txt prefixes data
      rows with ``#``.
  F2  String ids resolve as ``locale_key(n) = jenkins_lookup2("Global.Text." + str(n))``.
      Evidence: tools/locale/localedat.py; the literal ``Global.Text.%d`` is in
      H1Z1.exe 0.0.118.208059 at VA 0x143118638.  A string id of 0 means "unset".
  F3  Icon join: ``ICON_ID / IMAGE_SET_ID -> ImageSets.ID``, then
      ``ImageSetMappings(IMAGE_SET_ID, IMAGE_TYPE) -> Images.ID -> Images.FILE_NAME``.
      ``ImageSetMappings.IMAGE_TYPE`` is an FK to ``ImageSetTypes.ID`` whose DESCRIPTION
      gives the pixel height (3=8px, 4=16, 5=32, 6=64, 7=128, 8=256, 10=unbounded).
      Evidence: 5/5 Currency.ICON_ID values resolve end to end to
      icon_scrapCurrency / icon_crownCurrency / icon_skullCurrency / icon_freeCurrency /
      icon_stationCash.
  F4  ``ClientItemDefinitions.CODE_FACTORY_NAME`` is the item's behaviour class, and
      PARAM1..PARAM3 are per-class:
        RewardCrate        PARAM1 = reward set id, PARAM2 = key item id (0 = no key),
                           PARAM3 = wardrobe source image-set id
        LockedRewardCrate  PARAM1 = the RewardCrate item granted when unlocked
                           (33/33 resolve to a RewardCrate), PARAM2 = an opaque backend
                           bundle id (33 distinct, 169..335, resolves to nothing in the
                           client)
        RewardCrateKey     PARAM1 = the crate item this key opens
        GiveRewardSet /
        AccountGiveRewardSet   PARAM1 = reward set id
        GiveCurrency       PARAM1 = amount, PARAM2 = Currency.ID
        GiveItem           PARAM1 = granted item id, PARAM2 = count
        IncrementEntitlement   PARAM1 = entitlement id, PARAM2 = count
      Evidence: the crate/key round trip closes on 7 of the 14 keyed crates
      (crate.PARAM2 -> key, key.PARAM1 -> crate); the other 7 are client data bugs and are
      reported in ``crateKeyAnomalies``.  GiveCurrency PARAM2 is 1 on all 9 rows and 1 is
      Currency.ID for Scrap (F5).
  F5  Currency ids in this build: 1 Scrap, 4 Crowns, 5 Skulls, 6 Credits,
      7000 Daybreak Cash.  Evidence: Currency.NAME_ID through the locale (F2).
  F6  ``ClientItemDefinitions.SCRAP_VALUE_OVERRIDE`` is the Scrapyard payout in Scrap.
      ``-1`` means "not scrappable", ``0`` means "unset".  Evidence: it is near-perfectly
      determined by RARITY (5->20, 6->50, 7->200, 8->1000, 0->5) and is non-zero exactly on
      the item classes the client offers the ``ScrapAccountItem`` ("Scrap") use option on.
  F7  ``ClientItemDefinitions.GRINDER_REWARD_SET_ID`` on an *input* item names the reward
      set that grinding a set of that item's family produces.  Evidence: the 331 items that
      carry one fall into 36 groups, and every group is single-rarity and matches a
      cosmetic collection (e.g. set 4916 = 9 rarity-5 flannel shirts).
  F8  ``Grinders.txt`` is the whole client-side Scrapyard rule: RARITY_ID + INPUT_COUNT.
      3 rows: rarity 5 x5, rarity 6 x4, rarity 7 x5.  There is no grinder for rarity 8.
  F9  Item use options: ``ItemIdUseOptionGroupId(ITEM_ID) -> ItemUseOptionGroups
      (ITEM_USE_OPTION_GROUP_ID) -> ItemUseOptions.ITEM_USE_OPTION_ID``, whose TYPE_NAME is
      the client-side verb.  The economy verbs are OpenCrate, ScrapAccountItem,
      ShowGrinderUI, UseAccountGiveRewardSetItem, UseAccountRecipeItem and SkinItem.
 F10  ``ItemSourceLookup.txt`` is the wardrobe "where did this come from" label:
      ITEM_ID -> SOURCE_IMAGE_SET_ID (badge) + SOURCE_DESCRIPTION_ID (locale string, e.g.
      "Available in the Scrapyard", "Currently Retired").
 F11  ``MatchRankImageSets.txt`` is placement -> badge: the client picks the row whose
      MIN_RANK <= rank <= MAX_RANK and kills >= MIN_KILLS.  Row 1 is the rank-1/30-kill
      badge.  96 of the 201 IMAGE_SET_IDs are dangling in this build and are flagged.
 F12  ``StringHashToValue.txt`` carries the ranked-tier icon table
      (MatchRanking.TierImageId.1..7 and TierSubtierImageId.t.s / TierSubtierBarIcon.t.s for
      7 tiers x 5 subtiers) and ``Match.MaxFreeCurrency = 100``.

STREAMING: every sheet is read row by row through tools/data/sheet.py.  The largest input
here is ClientItemDefinitions.txt (2,643 x 74); the join tables it needs are small, so the
tool keeps only the columns it uses.

Usage:
    python tools/data/derive_economy.py
    python tools/data/derive_economy.py --data C:\Aug2017\out\data_aug \
        --out C:\Aug2017\out\data_aug\derived\economy.json --lang en_us
    python tools/data/derive_economy.py --summary      # counts + gaps only, no file write
"""

from __future__ import annotations

import argparse
import collections
import datetime
import json
import sys
from pathlib import Path
from typing import Any, Iterable

_TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(_TOOLS / "data"))
sys.path.insert(0, str(_TOOLS / "locale"))

from localedat import LocaleData  # noqa: E402
from sheet import Sheet  # noqa: E402

SCHEMA = "cranberry.economy/1"
CLIENT_BUILD = "0.0.118.208059"

DEFAULT_DATA = Path(r"C:\Aug2017\out\data_aug")
DEFAULT_OUT = Path(r"C:\Aug2017\out\data_aug\derived\economy.json")

#: CODE_FACTORY_NAME values this system owns (F4).
CRATE_FACTORIES = ("RewardCrate", "LockedRewardCrate")
BAG_FACTORIES = ("GiveRewardSet", "AccountGiveRewardSet")

#: ItemUseOptions.TYPE_NAME values that are economy verbs (F9).
ECONOMY_VERBS = {
    "OpenCrate",
    "ScrapAccountItem",
    "ShowGrinderUI",
    "UseAccountGiveRewardSetItem",
    "UseAccountRecipeItem",
    "SkinItem",
}

#: Per-field provenance.  Key is a dotted path into the output document; "[]" means "every
#: element of this list", and a path that ends at an object whose keys are data (an id-keyed
#: map such as ``iconFiles`` or ``tierImageSetIds``) covers every entry in that map.  The
#: value is "<sheet>.<column>", plus a note wherever the value is derived rather than copied.
#: ``selfcheck`` fails the run if an emitted field has no entry here.
PROVENANCE = {
    "currencies[].id": "Currency.ID",
    "currencies[].name": "Currency.NAME_ID -> locale en_us (F2)",
    "currencies[].nameStringId": "Currency.NAME_ID",
    "currencies[].descriptionStringId": "Currency.DESCRIPTION_ID (0 on every row in this build)",
    "currencies[].iconImageSetId": "Currency.ICON_ID",
    "currencies[].mapIconImageSetId": "Currency.MAP_ICON_ID",
    "currencies[].iconName": "ImageSets.DESCRIPTION for ICON_ID (F3)",
    "currencies[].iconFiles": "ImageSetMappings + Images.FILE_NAME keyed by ImageSetTypes pixel height (F3)",
    "currencies[].valueMax": "Currency.VALUE_MAX (0 on every row: no client-side cap)",
    "currencies[].scarcityValue": "Currency.SCARCITY_VALUE (0 on every row)",
    "currencies[].contentPackId": "Currency.CONTENT_ID",

    "rarities[].id": "ItemRarity.ID",
    "rarities[].colorName": "ItemRarity.DESCRIPTION",
    "rarities[].designerName": "ItemRarity.DESIGNER_NAME",
    "rarities[].clientColorId": "ItemRarity.CLIENT_COLOR_ID",
    "rarities[].modalScrapValue": "derived: most common ClientItemDefinitions.SCRAP_VALUE_OVERRIDE among items of this rarity, excluding 0 and -1 (F6)",
    "rarities[].scrapValueHistogram": "derived: count of each ClientItemDefinitions.SCRAP_VALUE_OVERRIDE at this rarity (F6)",
    "rarities[].grinderId": "derived: Grinders.ID whose RARITY_ID is this rarity (F8)",

    "scrapyard.grinders[].id": "Grinders.ID",
    "scrapyard.grinders[].rarityId": "Grinders.RARITY_ID",
    "scrapyard.grinders[].inputCount": "Grinders.INPUT_COUNT",
    "scrapyard.grinders[].rarityName": "ItemRarity.DESIGNER_NAME for RARITY_ID",
    "scrapyard.grinders[].inputItemCount": "derived: ClientItemDefinitions rows with this RARITY and a non-zero GRINDER_REWARD_SET_ID (F7)",
    "scrapyard.grinders[].rewardSetIds": "derived: distinct GRINDER_REWARD_SET_ID over those rows (F7)",
    "scrapyard.grinderInputFamilies[].rewardSetId": "ClientItemDefinitions.GRINDER_REWARD_SET_ID (F7)",
    "scrapyard.grinderInputFamilies[].rarityId": "ClientItemDefinitions.RARITY of the member items",
    "scrapyard.grinderInputFamilies[].rarityIds": "derived: every distinct ClientItemDefinitions.RARITY in the family (one entry on all 36 families)",
    "scrapyard.grinderInputFamilies[].itemIds": "ClientItemDefinitions.ID of every item pointing at this reward set",
    "scrapyard.grinderInputFamilies[].itemCount": "derived: len(itemIds)",
    "scrapyard.scrapValues[].name": "ClientItemDefinitions.NAME_ID -> locale en_us (F2)",
    "scrapyard.scrapValues[].codeFactoryName": "ClientItemDefinitions.CODE_FACTORY_NAME",
    "scrapyard.currencyPayoutItems[].name": "ClientItemDefinitions.NAME_ID -> locale en_us (F2); the 9 GiveCurrency rows have NAME_ID 0 and therefore no name",
    "scrapyard.currencyPayoutItems[].maxStackSize": "ClientItemDefinitions.MAX_STACK_SIZE",
    "scrapyard.universalCrateKeyItemId": "StringHashToValue 'AccountItems.RewardCrateKey1' - the key 5 crates share",
    "scrapyard.scrapValues[].itemId": "ClientItemDefinitions.ID",
    "scrapyard.scrapValues[].scrapValue": "ClientItemDefinitions.SCRAP_VALUE_OVERRIDE (-1 = not scrappable) (F6)",
    "scrapyard.scrapValues[].rarityId": "ClientItemDefinitions.RARITY",
    "scrapyard.scrapValues[].scrappableByClient": "derived: item has the ScrapAccountItem use option (F9)",
    "scrapyard.currencyPayoutItems[].itemId": "ClientItemDefinitions.ID where CODE_FACTORY_NAME = GiveCurrency",
    "scrapyard.currencyPayoutItems[].currencyId": "ClientItemDefinitions.PARAM2 (F4)",
    "scrapyard.currencyPayoutItems[].amount": "ClientItemDefinitions.PARAM1 (F4)",
    "scrapyard.itemClassSaleRewardSets[].itemClassId": "ItemClasses.ID",
    "scrapyard.itemClassSaleRewardSets[].saleRewardSetId": "ItemClasses.SALE_REWARD_SET_ID",
    "scrapyard.itemClassSaleRewardSets[].classScrapValue": "ItemClasses.SCRAP_VALUE",
    "scrapyard.storefrontItemIds": "ItemSourceLookup rows whose SOURCE_DESCRIPTION_ID resolves to 'Available in the Scrapyard' (F10)",
    "scrapyard.eventTicketScrapItemId": "StringHashToValue 'Currency.EventTicketScrapItemId'",

    "crates[].itemId": "ClientItemDefinitions.ID",
    "crates[].name": "ClientItemDefinitions.NAME_ID -> locale en_us (F2)",
    "crates[].kind": "derived from ClientItemDefinitions.CODE_FACTORY_NAME (RewardCrate / LockedRewardCrate)",
    "crates[].rewardSetId": "ClientItemDefinitions.PARAM1 for RewardCrate (F4); null for LockedRewardCrate",
    "crates[].keyItemId": "ClientItemDefinitions.PARAM2 for RewardCrate, 0 = opens without a key (F4)",
    "crates[].unlockedCrateItemId": "ClientItemDefinitions.PARAM1 for LockedRewardCrate (F4)",
    "crates[].backendBundleId": "ClientItemDefinitions.PARAM2 for LockedRewardCrate - opaque, resolves to nothing in the client (F4)",
    "crates[].sourceImageSetId": "ClientItemDefinitions.PARAM3 for RewardCrate; the wardrobe source badge shared with ItemSourceLookup (F4, F10)",
    "crates[].rarityId": "ClientItemDefinitions.RARITY",
    "crates[].scrapValue": "ClientItemDefinitions.SCRAP_VALUE_OVERRIDE (F6)",
    "crates[].accountScope": "ClientItemDefinitions.FLAG_ACCOUNT_SCOPE",
    "crates[].contentPackId": "ClientItemDefinitions.CONTENT_ID",
    "crates[].imageSetId": "ClientItemDefinitions.IMAGE_SET_ID",
    "crates[].useVerbs": "ItemIdUseOptionGroupId -> ItemUseOptionGroups -> ItemUseOptions.TYPE_NAME (F9)",

    "crateKeys[].itemId": "ClientItemDefinitions.ID where CODE_FACTORY_NAME = RewardCrateKey",
    "crateKeys[].opensItemId": "ClientItemDefinitions.PARAM1 (F4)",
    "crateKeys[].opensKind": "derived: CODE_FACTORY_NAME of PARAM1's item",
    "crateKeys[].name": "ClientItemDefinitions.NAME_ID -> locale en_us (F2)",
    "crateKeys[].scrapValue": "ClientItemDefinitions.SCRAP_VALUE_OVERRIDE (F6)",
    "crateKeys[].accountScope": "ClientItemDefinitions.FLAG_ACCOUNT_SCOPE",
    "crateKeys[].useVerbs": "ItemIdUseOptionGroupId -> ItemUseOptionGroups -> ItemUseOptions.TYPE_NAME (F9)",
    "crateKeyAnomalies[]": "derived: crates whose PARAM2 key does not point back at them (F4)",

    "bags[].itemId": "ClientItemDefinitions.ID where CODE_FACTORY_NAME in (GiveRewardSet, AccountGiveRewardSet)",
    "bags[].name": "ClientItemDefinitions.NAME_ID -> locale en_us (F2)",
    "bags[].kind": "derived: 'account' for AccountGiveRewardSet, 'inventory' for GiveRewardSet",
    "bags[].rewardSetId": "ClientItemDefinitions.PARAM1 (F4)",
    "bags[].rarityId": "ClientItemDefinitions.RARITY",
    "bags[].scrapValue": "ClientItemDefinitions.SCRAP_VALUE_OVERRIDE (F6)",
    "bags[].accountScope": "ClientItemDefinitions.FLAG_ACCOUNT_SCOPE",
    "bags[].useVerbs": "ItemIdUseOptionGroupId -> ItemUseOptionGroups -> ItemUseOptions.TYPE_NAME (F9)",

    "wardrobe.conversions[].conversionId": "AcctItemConversions.ID",
    "wardrobe.conversions[].accountItemId": "AcctItemConversions.ACCOUNT_ITEM_ID",
    "wardrobe.conversions[].rewardItemId": "AcctItemConversions.REWARD_ITEM_ID",
    "wardrobe.conversions[].rewardItemCount": "AcctItemConversions.REWARD_ITEM_COUNT (1 on all 691 rows)",
    "wardrobe.conversions[].rewardSetId": "AcctItemConversions.REWARD_SET_ID (0 on 555 of 691 rows)",
    "wardrobe.conversions[].tintId": "AcctItemConversions.REWARD_ITEM_TINT_ID (0 on all 691 rows; the Tint subsystem is vestigial PlanetSide 2 content)",
    "wardrobe.conversions[].materialEffectId": "AcctItemConversions.REWARD_ITEM_MATERIAL_EFFECT_ID (non-zero on 7 rows)",
    "wardrobe.conversions[].accountItemName": "ClientItemDefinitions.NAME_ID of ACCOUNT_ITEM_ID -> locale en_us (F2)",
    "wardrobe.conversions[].rewardItemName": "ClientItemDefinitions.NAME_ID of REWARD_ITEM_ID -> locale en_us (F2)",
    "wardrobe.conversions[].inputGroupIds": "AcctItemConversionGroupMappings.INPUT_GROUP_ID for this CONVERSION_ID",
    "wardrobe.conversions[].rewardRarityId": "ClientItemDefinitions.RARITY of REWARD_ITEM_ID",
    "wardrobe.conversions[].rewardEquipSlotId": "ClientItemDefinitions.PASSIVE_EQUIP_SLOT_ID of REWARD_ITEM_ID",
    "wardrobe.conversions[].rewardImageSetId": "ClientItemDefinitions.IMAGE_SET_ID of REWARD_ITEM_ID",
    "wardrobe.inputGroups[].groupId": "AcctItemConversionInputGroups.ID",
    "wardrobe.inputGroups[].itemIds": "AcctItemConversionInputItems.INPUT_ITEM_ID where CONVERSION_GROUP_ID = groupId",
    "wardrobe.inputGroups[].itemCount": "derived: len(itemIds)",
    "wardrobe.inputGroups[].declared": "derived: the group id appears in AcctItemConversionInputGroups.txt (true for all 40; 36 of them also have member rows in AcctItemConversionInputItems.txt, so 4 groups are declared but empty)",
    "wardrobe.inputGroups[].danglingItemIds": "derived: input item ids with no ClientItemDefinitions row (3 across all groups)",
    "wardrobe.sourceLabels[].itemId": "ItemSourceLookup.ITEM_ID (F10)",
    "wardrobe.sourceLabels[].sourceImageSetId": "ItemSourceLookup.SOURCE_IMAGE_SET_ID (F10)",
    "wardrobe.sourceLabels[].descriptionStringId": "ItemSourceLookup.SOURCE_DESCRIPTION_ID (F10)",
    "wardrobe.sourceLabels[].text": "ItemSourceLookup.SOURCE_DESCRIPTION_ID -> locale en_us (F2, F10)",
    "wardrobe.skinSlots[].slotId": "SkinItemSlot.SLOT_ID",
    "wardrobe.skinSlots[].name": "SkinItemSlot.SLOT_NAME_ID -> locale en_us",
    "wardrobe.skinSlots[].slotTypeId": "SkinItemSlot.SLOT_TYPE_ID (1 = apparel, 2 = weapon)",
    "wardrobe.skinSlots[].iconImageSetId": "SkinItemSlot.SLOT_ICON_ID",
    "wardrobe.skinSlots[].viewLocation": "SkinItemSlot.STATIC_VIEW_LOCATION",
    "wardrobe.skinSlots[].selectPreviewOnly": "SkinItemSlot.SELECT_PREVIEW_ONLY",

    "matchEnd.maxFreeCurrency": "StringHashToValue 'Match.MaxFreeCurrency' (F12)",
    "matchEnd.rankBadges[].id": "MatchRankImageSets.ID",
    "matchEnd.rankBadges[].minRank": "MatchRankImageSets.MIN_RANK (F11)",
    "matchEnd.rankBadges[].maxRank": "MatchRankImageSets.MAX_RANK (F11)",
    "matchEnd.rankBadges[].minKills": "MatchRankImageSets.MIN_KILLS (F11)",
    "matchEnd.rankBadges[].imageSetId": "MatchRankImageSets.IMAGE_SET_ID (F11)",
    "matchEnd.rankBadges[].imageIncludesRank": "MatchRankImageSets.IMAGE_INCLUDES_RANK (F11)",
    "matchEnd.rankBadges[].imageSetResolves": "derived: whether IMAGE_SET_ID exists in ImageSets.txt (F11)",
    "matchEnd.rankBadgesWithDanglingImageSet": "derived: how many rank badges point at an ImageSet id absent from ImageSets.txt (F11)",
    "matchEnd.rankedTiers.tierCount": "derived: number of 'MatchRanking.TierImageId.<t>' keys (F12)",
    "matchEnd.rankedTiers.subtierCount": "derived: number of distinct <s> in 'MatchRanking.TierSubtierImageId.<t>.<s>' (F12)",
    "matchEnd.rankedTiers.tierImageSetIds": "StringHashToValue 'MatchRanking.TierImageId.<t>' (F12)",
    "matchEnd.rankedTiers.subtierImageSetIds": "StringHashToValue 'MatchRanking.TierSubtierImageId.<t>.<s>' (F12)",
    "matchEnd.rankedTiers.subtierBarImageSetIds": "StringHashToValue 'MatchRanking.TierSubtierBarIcon.<t>.<s>' (F12)",
    "matchEnd.panels[].id": "MatchEndWindow.ID",
    "matchEnd.panels[].panelId": "MatchEndWindow.PANEL_ID",
    "matchEnd.panels[].gameModeId": "MatchEndWindow.GAME_MODE_ID (0 and 29 are not in GameModeDefinitions; 0 is the fallback layout)",
    "matchEnd.panels[].displayOrder": "MatchEndWindow.DISPLAY_ORDER",
    "matchEnd.panels[].x": "MatchEndWindow.X",
    "matchEnd.panels[].y": "MatchEndWindow.Y",
    "matchEnd.panels[].delaySeconds": "MatchEndWindow.DELAY",
    "matchEnd.panels[].clientRequirementId": "MatchEndWindow.CLIENT_REQUIREMENT (definition sheet is not present in this build)",
    "matchEnd.teamColors[].teamId": "InvitationalTeamInfo.TEAM_ID",
    "matchEnd.teamColors[].rgba": "InvitationalTeamInfo.COLOR_0..COLOR_3 (floats 0..1)",
    "matchEnd.teamColors[].secondaryRgba": "InvitationalTeamInfo.SECONDARY_COLOR_0..3 (all-zero = unset)",
    "matchEnd.squadMarkerColors[].memberId": "GroupMemberColorMappings.MEMBER_ID",
    "matchEnd.squadMarkerColors[].colorId": "GroupMemberColorMappings.COLOR_ID",
    "matchEnd.panelGameModeIdsNotInGameModeDefinitions": "derived: MatchEndWindow.GAME_MODE_ID values with no GameModeDefinitions row (0 = fallback layout, 29 = unknown)",
    "matchEnd.gameModes[].id": "GameModeDefinitions.ID",
    "matchEnd.gameModes[].designName": "GameModeDefinitions.DESIGN_NAME",
    "matchEnd.gameModes[].gameModeTypeId": "GameModeDefinitions.GAME_MODE_TYPE_ID (1 = battle royale, 6 = training)",
    "matchEnd.gameModes[].title": "GameModeDefinitions.TITLE_STRING_ID -> locale en_us (F2); set on the Skirmish row only",
    "matchEnd.gameModes[].uiTag": "GameModeDefinitions.UI_TAG",
    "matchEnd.experienceAwards[].id": "Experience.ID",
    "matchEnd.experienceAwards[].awardTypeId": "Experience.AWARD_TYPE_ID",
    "matchEnd.experienceAwards[].xp": "Experience.XP",
    "matchEnd.experienceAwards[].notableEvent": "Experience.NOTABLE_EVENT",
    "matchEnd.experienceAwards[].label": "Experience.STRING_ID -> locale en_us (F2); most rows are legacy H1Z1 survival awards with no live string",
    "matchEnd.experienceThresholds": "StringHashToValue 'Experience.*'",

    "itemSortTypes[].id": "ItemSortTypes.ID",
    "itemSortTypes[].name": "ItemSortTypes.NAME_ID -> locale en_us (F2)",
    "itemSortTypes[].displayOrder": "ItemSortTypes.DISPLAY_ORDER",
    "contentPacks.liveKotkUnlocked": "ContentPacks.ID where LIVE_KOTK_UNLOCKED = 1",
    "contentPacks.all": "ContentPacks.ID (DESCRIPTION and DESIGNER_NAME are blank on every row)",
    "contentPacks.note": "this tool's own remark about ContentPacks.txt",
    "useVerbs[].useOptionId": "ItemUseOptions.ITEM_USE_OPTION_ID (F9)",
    "useVerbs[].typeName": "ItemUseOptions.TYPE_NAME (F9)",
    "useVerbs[].label": "ItemUseOptions.NAME_ID -> locale en_us (F2)",
    "useVerbs[].itemCount": "derived: items mapped to a group containing this option (F9)",
}


# -- small helpers ---------------------------------------------------------------------


def _int(value: str, default: int = 0) -> int:
    value = (value or "").strip()
    if not value:
        return default
    try:
        return int(value)
    except ValueError:
        return default


def _float(value: str, default: float = 0.0) -> float:
    value = (value or "").strip()
    if not value:
        return default
    try:
        return float(value)
    except ValueError:
        return default


class Data:
    """Lazy, cached access to the extracted datasheets in one directory."""

    def __init__(self, root: Path, lang: str) -> None:
        self.root = root
        self.locale = LocaleData(lang)
        self._cache: dict[str, list[dict[str, str]]] = {}

    def rows(self, name: str) -> list[dict[str, str]]:
        if name not in self._cache:
            path = self.root / name
            if not path.is_file():
                sys.exit(f"missing input sheet: {path}")
            self._cache[name] = list(Sheet(path))
        return self._cache[name]

    def text(self, string_id: int | str) -> str | None:
        """Resolve a datasheet string id; 0/blank means 'unset', not a key (F2)."""
        sid = _int(string_id) if isinstance(string_id, str) else string_id
        if sid == 0:
            return None
        return self.locale.text(sid)


class IconIndex:
    """ImageSet -> concrete texture files (F3)."""

    def __init__(self, data: Data) -> None:
        self.set_names = {_int(r["ID"]): r["DESCRIPTION"] for r in data.rows("ImageSets.txt")}
        self.images = {_int(r["ID"]): r["FILE_NAME"] for r in data.rows("Images.txt")}
        # ImageSetTypes.DESCRIPTION is the human pixel-height label; use it as the key.
        self.types = {_int(r["ID"]): r["DESCRIPTION"] for r in data.rows("ImageSetTypes.txt")}
        self.mappings: dict[int, dict[str, str]] = collections.defaultdict(dict)
        for r in data.rows("ImageSetMappings.txt"):
            set_id = _int(r["IMAGE_SET_ID"])
            image = self.images.get(_int(r["IMAGE_ID"]))
            if image:
                label = self.types.get(_int(r["IMAGE_TYPE"]), f"type{r['IMAGE_TYPE']}")
                self.mappings[set_id][label] = image

    def name(self, set_id: int) -> str | None:
        return self.set_names.get(set_id)

    def files(self, set_id: int) -> dict[str, str]:
        return dict(sorted(self.mappings.get(set_id, {}).items()))

    def exists(self, set_id: int) -> bool:
        return set_id in self.set_names


class UseVerbIndex:
    """Item -> client-side verbs, via the option-group indirection (F9)."""

    def __init__(self, data: Data) -> None:
        self.options = {
            _int(r["ITEM_USE_OPTION_ID"]): (
                r["TYPE_NAME"],
                data.text(r["NAME_ID"]),
            )
            for r in data.rows("ItemUseOptions.txt")
        }
        self.groups: dict[int, list[int]] = collections.defaultdict(list)
        for r in data.rows("ItemUseOptionGroups.txt"):
            self.groups[_int(r["ITEM_USE_OPTION_GROUP_ID"])].append(_int(r["ITEM_USE_OPTION_ID"]))
        self.item_group = {
            _int(r["ITEM_ID"]): _int(r["ITEM_USE_OPTION_GROUP_ID"])
            for r in data.rows("ItemIdUseOptionGroupId.txt")
        }

    def verbs(self, item_id: int) -> list[str]:
        group = self.item_group.get(item_id)
        if group is None:
            return []
        seen: list[str] = []
        for option in self.groups.get(group, ()):
            entry = self.options.get(option)
            if entry and entry[0] not in seen:
                seen.append(entry[0])
        return seen

    def economy_verbs(self, item_id: int) -> list[str]:
        return [v for v in self.verbs(item_id) if v in ECONOMY_VERBS]

    def has(self, item_id: int, type_name: str) -> bool:
        return type_name in self.verbs(item_id)

    def item_count_for(self, option_id: int) -> int:
        groups = {g for g, opts in self.groups.items() if option_id in opts}
        return sum(1 for g in self.item_group.values() if g in groups)


# -- sections --------------------------------------------------------------------------


def build_currencies(data: Data, icons: IconIndex) -> list[dict[str, Any]]:
    out = []
    for r in data.rows("Currency.txt"):
        icon = _int(r["ICON_ID"])
        out.append({
            "id": _int(r["ID"]),
            "name": data.text(r["NAME_ID"]),
            "nameStringId": _int(r["NAME_ID"]),
            "descriptionStringId": _int(r["DESCRIPTION_ID"]),
            "iconImageSetId": icon,
            "mapIconImageSetId": _int(r["MAP_ICON_ID"]),
            "iconName": icons.name(icon),
            "iconFiles": icons.files(icon),
            "valueMax": _int(r["VALUE_MAX"]),
            "scarcityValue": _int(r["SCARCITY_VALUE"]),
            "contentPackId": _int(r["CONTENT_ID"]),
        })
    return sorted(out, key=lambda c: c["id"])


def build_rarities(data: Data, items: list[dict[str, str]]) -> list[dict[str, Any]]:
    grinder_by_rarity = {_int(r["RARITY_ID"]): _int(r["ID"]) for r in data.rows("Grinders.txt")}
    hist: dict[int, collections.Counter[int]] = collections.defaultdict(collections.Counter)
    for r in items:
        hist[_int(r["RARITY"])][_int(r["SCRAP_VALUE_OVERRIDE"])] += 1

    out = []
    for r in data.rows("ItemRarity.txt"):
        rid = _int(r["ID"])
        counts = hist.get(rid, collections.Counter())
        # The modal payout ignores 0 ("unset") and -1 ("not scrappable") (F6).
        payouts = {v: n for v, n in counts.items() if v > 0}
        out.append({
            "id": rid,
            "colorName": r["DESCRIPTION"],
            "designerName": r["DESIGNER_NAME"],
            "clientColorId": _int(r["CLIENT_COLOR_ID"]),
            "modalScrapValue": max(payouts, key=payouts.get) if payouts else None,
            "scrapValueHistogram": {str(v): n for v, n in sorted(counts.items())},
            "grinderId": grinder_by_rarity.get(rid),
        })
    return sorted(out, key=lambda r: r["id"])


def build_scrapyard(
    data: Data, items: list[dict[str, str]], by_id: dict[int, dict[str, str]],
    verbs: UseVerbIndex,
) -> dict[str, Any]:
    rarity_name = {_int(r["ID"]): r["DESIGNER_NAME"] for r in data.rows("ItemRarity.txt")}

    # Grinder input families: every item that names a grinder reward set (F7).
    families: dict[int, dict[str, Any]] = {}
    for r in items:
        rs = _int(r["GRINDER_REWARD_SET_ID"])
        if rs == 0:
            continue
        fam = families.setdefault(rs, {
            "rewardSetId": rs,
            "rarityId": _int(r["RARITY"]),
            "rarityIds": set(),
            "itemIds": [],
        })
        fam["rarityIds"].add(_int(r["RARITY"]))
        fam["itemIds"].append(_int(r["ID"]))
    family_list = []
    for rs in sorted(families):
        fam = families[rs]
        rarities = sorted(fam.pop("rarityIds"))
        fam["rarityId"] = rarities[0] if len(rarities) == 1 else None
        fam["rarityIds"] = rarities
        fam["itemIds"] = sorted(fam["itemIds"])
        fam["itemCount"] = len(fam["itemIds"])
        family_list.append(fam)

    grinders = []
    for r in data.rows("Grinders.txt"):
        rid = _int(r["RARITY_ID"])
        member_sets = sorted({
            _int(i["GRINDER_REWARD_SET_ID"]) for i in items
            if _int(i["RARITY"]) == rid and _int(i["GRINDER_REWARD_SET_ID"]) != 0
        })
        member_items = [
            _int(i["ID"]) for i in items
            if _int(i["RARITY"]) == rid and _int(i["GRINDER_REWARD_SET_ID"]) != 0
        ]
        grinders.append({
            "id": _int(r["ID"]),
            "rarityId": rid,
            "rarityName": rarity_name.get(rid),
            "inputCount": _int(r["INPUT_COUNT"]),
            "inputItemCount": len(member_items),
            "rewardSetIds": member_sets,
        })

    # Scrap payouts.  Emit only items that actually carry a value or the client verb (F6).
    scrap_values = []
    for r in items:
        value = _int(r["SCRAP_VALUE_OVERRIDE"])
        item_id = _int(r["ID"])
        scrappable = verbs.has(item_id, "ScrapAccountItem")
        if value == 0 and not scrappable:
            continue
        scrap_values.append({
            "itemId": item_id,
            "name": data.text(r["NAME_ID"]),
            "rarityId": _int(r["RARITY"]),
            "scrapValue": value,
            "scrappableByClient": scrappable,
            "codeFactoryName": r["CODE_FACTORY_NAME"],
        })

    payouts = []
    for r in items:
        if r["CODE_FACTORY_NAME"] != "GiveCurrency":
            continue
        payouts.append({
            "itemId": _int(r["ID"]),
            "name": data.text(r["NAME_ID"]),
            "currencyId": _int(r["PARAM2"]),
            "amount": _int(r["PARAM1"]),
            "maxStackSize": _int(r["MAX_STACK_SIZE"]),
        })

    class_sets = []
    for r in data.rows("ItemClasses.txt"):
        sale = _int(r["SALE_REWARD_SET_ID"])
        scrap = _int(r["SCRAP_VALUE"])
        if sale == 0 and scrap == 0:
            continue
        class_sets.append({
            "itemClassId": _int(r["ID"]),
            "saleRewardSetId": sale or None,
            "classScrapValue": scrap or None,
        })

    # Wardrobe source label "Available in the Scrapyard" (F10).
    storefront = []
    for r in data.rows("ItemSourceLookup.txt"):
        label = data.text(r["SOURCE_DESCRIPTION_ID"]) or ""
        if "Scrapyard" in label:
            storefront.append(_int(r["ITEM_ID"]))

    hashes = {r["STRING"]: r["VALUE"] for r in data.rows("StringHashToValue.txt")}

    return {
        "grinders": grinders,
        "grinderInputFamilies": family_list,
        "scrapValues": sorted(scrap_values, key=lambda s: s["itemId"]),
        "currencyPayoutItems": sorted(payouts, key=lambda p: p["itemId"]),
        "itemClassSaleRewardSets": sorted(class_sets, key=lambda c: c["itemClassId"]),
        "storefrontItemIds": sorted(set(storefront)),
        "eventTicketScrapItemId": _int(hashes.get("Currency.EventTicketScrapItemId", "0")) or None,
        "universalCrateKeyItemId": _int(hashes.get("AccountItems.RewardCrateKey1", "0")) or None,
    }


def build_crates(
    data: Data, items: list[dict[str, str]], by_id: dict[int, dict[str, str]],
    verbs: UseVerbIndex,
) -> tuple[list[dict[str, Any]], list[dict[str, Any]], list[dict[str, Any]], list[dict[str, Any]]]:
    crates: list[dict[str, Any]] = []
    keys: list[dict[str, Any]] = []
    bags: list[dict[str, Any]] = []

    for r in items:
        factory = r["CODE_FACTORY_NAME"]
        item_id = _int(r["ID"])
        common = {
            "itemId": item_id,
            "name": data.text(r["NAME_ID"]),
            "rarityId": _int(r["RARITY"]),
            "scrapValue": _int(r["SCRAP_VALUE_OVERRIDE"]),
            "accountScope": r["FLAG_ACCOUNT_SCOPE"] == "1",
            "contentPackId": _int(r["CONTENT_ID"]),
            "imageSetId": _int(r["IMAGE_SET_ID"]),
            "useVerbs": verbs.economy_verbs(item_id),
        }
        if factory == "RewardCrate":
            crates.append({
                **common,
                "kind": "reward",
                "rewardSetId": _int(r["PARAM1"]) or None,
                "keyItemId": _int(r["PARAM2"]) or None,
                "unlockedCrateItemId": None,
                "backendBundleId": None,
                "sourceImageSetId": _int(r["PARAM3"]) or None,
            })
        elif factory == "LockedRewardCrate":
            crates.append({
                **common,
                "kind": "locked",
                "rewardSetId": None,
                "keyItemId": None,
                "unlockedCrateItemId": _int(r["PARAM1"]) or None,
                "backendBundleId": _int(r["PARAM2"]) or None,
                "sourceImageSetId": None,
            })
        elif factory == "RewardCrateKey":
            target = _int(r["PARAM1"])
            keys.append({
                "itemId": item_id,
                "name": data.text(r["NAME_ID"]),
                "opensItemId": target or None,
                "opensKind": by_id.get(target, {}).get("CODE_FACTORY_NAME"),
                "scrapValue": _int(r["SCRAP_VALUE_OVERRIDE"]),
                "accountScope": r["FLAG_ACCOUNT_SCOPE"] == "1",
                "useVerbs": verbs.economy_verbs(item_id),
            })
        elif factory in BAG_FACTORIES:
            bags.append({
                "itemId": item_id,
                "name": data.text(r["NAME_ID"]),
                "kind": "account" if factory == "AccountGiveRewardSet" else "inventory",
                "rewardSetId": _int(r["PARAM1"]) or None,
                "rarityId": _int(r["RARITY"]),
                "scrapValue": _int(r["SCRAP_VALUE_OVERRIDE"]),
                "accountScope": r["FLAG_ACCOUNT_SCOPE"] == "1",
                "useVerbs": verbs.economy_verbs(item_id),
            })

    # Crate <-> key round trip (F4): report the rows where it does not close.
    key_target = {k["itemId"]: k["opensItemId"] for k in keys}
    anomalies = []
    for c in crates:
        if c["kind"] != "reward" or not c["keyItemId"]:
            continue
        back = key_target.get(c["keyItemId"])
        if back != c["itemId"]:
            anomalies.append({
                "crateItemId": c["itemId"],
                "crateName": c["name"],
                "keyItemId": c["keyItemId"],
                "keyPointsBackAt": back,
                "note": "RewardCrate.PARAM2 names a key whose PARAM1 does not name this crate",
            })

    return (
        sorted(crates, key=lambda c: c["itemId"]),
        sorted(keys, key=lambda k: k["itemId"]),
        sorted(bags, key=lambda b: b["itemId"]),
        anomalies,
    )


def build_wardrobe(
    data: Data, by_id: dict[int, dict[str, str]],
) -> dict[str, Any]:
    group_of: dict[int, list[int]] = collections.defaultdict(list)
    for r in data.rows("AcctItemConversionGroupMappings.txt"):
        group_of[_int(r["CONVERSION_ID"])].append(_int(r["INPUT_GROUP_ID"]))

    group_items: dict[int, list[int]] = collections.defaultdict(list)
    for r in data.rows("AcctItemConversionInputItems.txt"):
        group_items[_int(r["CONVERSION_GROUP_ID"])].append(_int(r["INPUT_ITEM_ID"]))

    declared_groups = {_int(r["ID"]) for r in data.rows("AcctItemConversionInputGroups.txt")}

    conversions = []
    for r in data.rows("AcctItemConversions.txt"):
        cid = _int(r["ID"])
        reward = _int(r["REWARD_ITEM_ID"])
        reward_row = by_id.get(reward, {})
        conversions.append({
            "conversionId": cid,
            "accountItemId": _int(r["ACCOUNT_ITEM_ID"]),
            "accountItemName": data.text(by_id.get(_int(r["ACCOUNT_ITEM_ID"]), {}).get("NAME_ID", "0")),
            "rewardItemId": reward or None,
            "rewardItemName": data.text(reward_row.get("NAME_ID", "0")),
            "rewardItemCount": _int(r["REWARD_ITEM_COUNT"]),
            "rewardSetId": _int(r["REWARD_SET_ID"]) or None,
            "tintId": _int(r["REWARD_ITEM_TINT_ID"]) or None,
            "materialEffectId": _int(r["REWARD_ITEM_MATERIAL_EFFECT_ID"]) or None,
            "rewardRarityId": _int(reward_row.get("RARITY", "0")) if reward_row else None,
            "rewardEquipSlotId": _int(reward_row.get("PASSIVE_EQUIP_SLOT_ID", "0")) or None,
            "rewardImageSetId": _int(reward_row.get("IMAGE_SET_ID", "0")) or None,
            "inputGroupIds": sorted(set(group_of.get(cid, ()))),
        })

    input_groups = []
    for gid in sorted(declared_groups | set(group_items)):
        members = sorted(set(group_items.get(gid, ())))
        input_groups.append({
            "groupId": gid,
            "declared": gid in declared_groups,
            "itemIds": members,
            "itemCount": len(members),
            "danglingItemIds": [i for i in members if i not in by_id],
        })

    labels = []
    for r in data.rows("ItemSourceLookup.txt"):
        labels.append({
            "itemId": _int(r["ITEM_ID"]),
            "sourceImageSetId": _int(r["SOURCE_IMAGE_SET_ID"]),
            "descriptionStringId": _int(r["SOURCE_DESCRIPTION_ID"]),
            "text": data.text(r["SOURCE_DESCRIPTION_ID"]),
        })

    slots = []
    for r in data.rows("SkinItemSlot.txt"):
        slots.append({
            "slotId": _int(r["SLOT_ID"]),
            "slotTypeId": _int(r["SLOT_TYPE_ID"]),
            "name": data.text(r["SLOT_NAME_ID"]),
            "iconImageSetId": _int(r["SLOT_ICON_ID"]) or None,
            "viewLocation": r["STATIC_VIEW_LOCATION"],
            "selectPreviewOnly": r["SELECT_PREVIEW_ONLY"] == "1",
        })

    return {
        "conversions": sorted(conversions, key=lambda c: c["conversionId"]),
        "inputGroups": input_groups,
        "sourceLabels": sorted(labels, key=lambda l: l["itemId"]),
        "skinSlots": sorted(slots, key=lambda s: s["slotId"]),
    }


def build_match_end(data: Data, icons: IconIndex) -> dict[str, Any]:
    badges = []
    for r in data.rows("MatchRankImageSets.txt"):
        set_id = _int(r["IMAGE_SET_ID"])
        badges.append({
            "id": _int(r["ID"]),
            "minRank": _int(r["MIN_RANK"]),
            "maxRank": _int(r["MAX_RANK"]),
            "minKills": _int(r["MIN_KILLS"]),
            "imageSetId": set_id,
            "imageIncludesRank": r["IMAGE_INCLUDES_RANK"] == "1",
            "imageSetResolves": icons.exists(set_id),
        })

    hashes = {r["STRING"]: r["VALUE"] for r in data.rows("StringHashToValue.txt")}
    tier_images: dict[str, int] = {}
    subtier_images: dict[str, int] = {}
    subtier_bars: dict[str, int] = {}
    for key, value in hashes.items():
        if key.startswith("MatchRanking.TierImageId."):
            tier_images[key.rsplit(".", 1)[1]] = _int(value)
        elif key.startswith("MatchRanking.TierSubtierImageId."):
            subtier_images[key[len("MatchRanking.TierSubtierImageId."):]] = _int(value)
        elif key.startswith("MatchRanking.TierSubtierBarIcon."):
            subtier_bars[key[len("MatchRanking.TierSubtierBarIcon."):]] = _int(value)

    panels = []
    for r in data.rows("MatchEndWindow.txt"):
        panels.append({
            "id": _int(r["ID"]),
            "panelId": _int(r["PANEL_ID"]),
            "gameModeId": _int(r["GAME_MODE_ID"]),
            "displayOrder": _int(r["DISPLAY_ORDER"]),
            "x": _int(r["X"]),
            "y": _int(r["Y"]),
            "delaySeconds": _float(r["DELAY"]),
            "clientRequirementId": _int(r["CLIENT_REQUIREMENT"]) or None,
        })

    team_colors = []
    for r in data.rows("InvitationalTeamInfo.txt"):
        rgba = [_float(r[f"COLOR_{i}"]) for i in range(4)]
        secondary = [_float(r[f"SECONDARY_COLOR_{i}"]) for i in range(4)]
        team_colors.append({
            "teamId": _int(r["TEAM_ID"]),
            "rgba": rgba,
            "secondaryRgba": secondary if any(secondary) else None,
        })

    squad = [
        {"memberId": _int(r["MEMBER_ID"]), "colorId": _int(r["COLOR_ID"])}
        for r in data.rows("GroupMemberColorMappings.txt")
    ]

    modes = []
    for r in data.rows("GameModeDefinitions.txt"):
        modes.append({
            "id": _int(r["ID"]),
            "designName": r["DESIGN_NAME"],
            "gameModeTypeId": _int(r["GAME_MODE_TYPE_ID"]),
            "uiTag": r["UI_TAG"],
            "title": data.text(r["TITLE_STRING_ID"]),
        })
    known_modes = {m["id"] for m in modes}

    awards = []
    for r in data.rows("Experience.txt"):
        awards.append({
            "id": _int(r["ID"]),
            "awardTypeId": _int(r["AWARD_TYPE_ID"]),
            "xp": _int(r["XP"]),
            "notableEvent": r["NOTABLE_EVENT"] == "1",
            "label": data.text(r["STRING_ID"]),
        })

    return {
        "maxFreeCurrency": _int(hashes.get("Match.MaxFreeCurrency", "0")) or None,
        "rankBadges": sorted(badges, key=lambda b: b["id"]),
        "rankBadgesWithDanglingImageSet": sum(1 for b in badges if not b["imageSetResolves"]),
        "rankedTiers": {
            "tierCount": len(tier_images),
            "subtierCount": len({k.split(".")[1] for k in subtier_images}) if subtier_images else 0,
            "tierImageSetIds": dict(sorted(tier_images.items(), key=lambda kv: int(kv[0]))),
            "subtierImageSetIds": dict(sorted(subtier_images.items())),
            "subtierBarImageSetIds": dict(sorted(subtier_bars.items())),
        },
        "panels": sorted(panels, key=lambda p: p["id"]),
        "panelGameModeIdsNotInGameModeDefinitions": sorted(
            {p["gameModeId"] for p in panels if p["gameModeId"] not in known_modes}
        ),
        "teamColors": sorted(team_colors, key=lambda t: t["teamId"]),
        "squadMarkerColors": sorted(squad, key=lambda s: s["memberId"]),
        "gameModes": sorted(modes, key=lambda m: m["id"]),
        "experienceAwards": sorted(awards, key=lambda a: a["id"]),
        "experienceThresholds": {
            k.split(".", 1)[1]: _int(v) for k, v in sorted(hashes.items())
            if k.startswith("Experience.")
        },
    }


def build_server_side_gaps(
    data: Data, items: list[dict[str, str]], crates: list[dict[str, Any]],
    bags: list[dict[str, Any]], wardrobe: dict[str, Any],
) -> list[dict[str, Any]]:
    """
    Every value a Cranberry economy needs that the August client genuinely does not carry.
    Nothing here is guessed; each entry says what is missing and how it was established.
    """
    # Reward set ids referenced from every direction, with no membership table anywhere.
    referenced: dict[str, set[int]] = collections.defaultdict(set)
    for c in crates:
        if c["rewardSetId"]:
            referenced["crate"].add(c["rewardSetId"])
    for b in bags:
        if b["rewardSetId"]:
            referenced["bag"].add(b["rewardSetId"])
    for r in items:
        rs = _int(r["GRINDER_REWARD_SET_ID"])
        if rs:
            referenced["grinder"].add(rs)
    for r in data.rows("ItemClasses.txt"):
        rs = _int(r["SALE_REWARD_SET_ID"])
        if rs:
            referenced["itemClassSale"].add(rs)
    for c in wardrobe["conversions"]:
        if c["rewardSetId"]:
            referenced["wardrobeConversion"].add(c["rewardSetId"])
    for r in data.rows("ChronicleLevels.txt"):
        rs = _int(r["REWARD_SET_ID"])
        if rs:
            referenced["chronicleLevel"].add(rs)
    all_sets = sorted(set().union(*referenced.values())) if referenced else []

    locked_bundles = sorted({c["backendBundleId"] for c in crates if c["backendBundleId"]})

    return [
        {
            "id": "rewardSetContents",
            "need": "The member items, counts and per-item weights of every reward set - what a "
                    "crate, a bag, a Scrapyard exchange or an item-class sale actually grants.",
            "why": "No sheet in the 50,502-entry August asset index defines reward-set membership. "
                   "The ids are referenced from five directions and resolve to nothing: they are "
                   "not ClientItemDefinitions ids and they do not cross-reference each other.",
            "referencedSetCount": len(all_sets),
            "referencedSetIds": all_sets,
            "referencedFrom": {k: sorted(v) for k, v in sorted(referenced.items())},
            "cranberryMustAuthor": "one table rewardSetId -> [(itemId, count, weight)]",
        },
        {
            "id": "crateDropOdds",
            "need": "The per-rarity and per-item probability of a crate roll.",
            "why": "RewardCrate rows carry only a reward-set id. No odds, weights or pity counters "
                   "appear in any client sheet; the client renders whatever the server sends back.",
            "cranberryMustAuthor": "a weighted roll per crate, plus the rarity distribution it should "
                                   "produce",
        },
        {
            "id": "crownPrices",
            "need": "The Crowns price of anything purchasable.",
            "why": "ClientItemDefinitions.COST is 0 on 2,352 of 2,643 rows and 1 on the rest, and "
                   "CURRENCY_TYPE is 0 on 2,627 rows (5 = Skulls on 10, 1 = Scrap on 1, -1 on 5). "
                   "ClientStorePortalCategories.txt is header-only with 0 rows, and "
                   "ClientStoreBillboardPanels shortcut_type/shortcut_value point at a backend "
                   "store this build cannot reach. Prices were never client-side.",
            "cranberryMustAuthor": "a price list itemId -> (currencyId, amount)",
        },
        {
            "id": "currencyGrantRules",
            "need": "How much Crowns/Scrap/Skulls a match awards, and for what.",
            "why": "The only client-side figure is StringHashToValue 'Match.MaxFreeCurrency' = 100, "
                   "which is a cap, not a curve. Currency.VALUE_MAX and SCARCITY_VALUE are 0 on all "
                   "five rows, so the client imposes no balance cap either.",
            "cranberryMustAuthor": "a per-placement / per-kill currency award table and any cap",
        },
        {
            "id": "grinderOutputSelection",
            "need": "Which item the Scrapyard hands back for a completed exchange.",
            "why": "Grinders.txt gives only (rarityId, inputCount) for rarities 5, 6 and 7 - there is "
                   "no grinder for rarity 8 - and GRINDER_REWARD_SET_ID on the input items names a "
                   "reward set whose contents are absent (see rewardSetContents).",
            "clientSideRule": "accept INPUT_COUNT items of RARITY_ID, then grant one item from the "
                              "reward set the consumed items name",
            "cranberryMustAuthor": "the output pool per grinder reward set and the pick rule",
        },
        {
            "id": "lockedCrateBackendBundleIds",
            "need": "The meaning of LockedRewardCrate.PARAM2.",
            "why": "33 distinct sequential values in 169..335, one per locked crate. They are not "
                   "ClientItemDefinitions ids, ImageSets ids or Images ids, and no sheet in the "
                   "extraction defines them. They look like backend store/bundle handles.",
            "observedValues": locked_bundles,
            "cranberryMustAuthor": "nothing, unless a Cranberry store reuses the id space; the "
                                   "unlock path is fully described by PARAM1",
        },
        {
            "id": "clientRequirementDefinitions",
            "need": "The gate behind MatchEndWindow.CLIENT_REQUIREMENT (ids 2348, 2349, 2367, "
                    "2377-2381, 2385, 2386).",
            "why": "Ten match-end panels are gated on a requirement id whose definition sheet is not "
                   "present in this build's extraction. The panels are ranked-mode and "
                   "invitational-mode extras.",
            "cranberryMustAuthor": "either satisfy or suppress those panels explicitly",
        },
        {
            "id": "rankedTierThresholds",
            "need": "The score/points boundaries between the 7 ranked tiers and their 5 subtiers.",
            "why": "StringHashToValue carries only the tier and subtier ICON ids "
                   "(MatchRanking.TierImageId.1..7, TierSubtierImageId.t.s, TierSubtierBarIcon.t.s). "
                   "No threshold, no decay rule and no season length appears in any sheet.",
            "cranberryMustAuthor": "tier/subtier boundaries and the rating update rule",
        },
        {
            "id": "accountEntitlementIds",
            "need": "What entitlement ids 105005 (Event Ticket) and 2008395 (Battle Royale 30-Day "
                    "Pass) unlock.",
            "why": "IncrementEntitlement items carry PARAM1 = an entitlement id and PARAM2 = a count, "
                   "but no *Entitlement* sheet exists anywhere in the asset index.",
            "cranberryMustAuthor": "an entitlement model, or drop the feature",
        },
        {
            "id": "wardrobeConversionCost",
            "need": "What a wardrobe conversion charges, if anything.",
            "why": "AcctItemConversions gives ACCOUNT_ITEM_ID -> REWARD_ITEM_ID and the input-item "
                   "groups that may be consumed, but no cost column and no consumed-count column. "
                   "REWARD_ITEM_COUNT is 1 on all 691 rows.",
            "cranberryMustAuthor": "the consume rule (how many input-group items a conversion eats) "
                                   "and any currency cost",
        },
    ]


# -- assembly --------------------------------------------------------------------------


def derive(data_dir: Path, lang: str) -> dict[str, Any]:
    data = Data(data_dir, lang)
    icons = IconIndex(data)
    verbs = UseVerbIndex(data)

    items = data.rows("ClientItemDefinitions.txt")
    by_id = {_int(r["ID"]): r for r in items}

    currencies = build_currencies(data, icons)
    rarities = build_rarities(data, items)
    scrapyard = build_scrapyard(data, items, by_id, verbs)
    crates, keys, bags, anomalies = build_crates(data, items, by_id, verbs)
    wardrobe = build_wardrobe(data, by_id)
    match_end = build_match_end(data, icons)
    gaps = build_server_side_gaps(data, items, crates, bags, wardrobe)

    sort_types = [
        {
            "id": _int(r["ID"]),
            "name": data.text(r["NAME_ID"]),
            "displayOrder": _int(r["DISPLAY_ORDER"]),
        }
        for r in data.rows("ItemSortTypes.txt")
    ]

    packs = data.rows("ContentPacks.txt")
    content_packs = {
        "all": sorted(_int(r["ID"]) for r in packs),
        "liveKotkUnlocked": sorted(_int(r["ID"]) for r in packs if r.get("LIVE_KOTK_UNLOCKED") == "1"),
        "note": "DESCRIPTION and DESIGNER_NAME are blank on every row in this build.",
    }

    use_verbs = []
    for option_id, (type_name, label) in sorted(verbs.options.items()):
        if type_name not in ECONOMY_VERBS:
            continue
        use_verbs.append({
            "useOptionId": option_id,
            "typeName": type_name,
            "label": label,
            "itemCount": verbs.item_count_for(option_id),
        })

    doc: dict[str, Any] = {
        "schema": SCHEMA,
        "generatedAt": datetime.datetime.now(datetime.timezone.utc)
                                .replace(microsecond=0).isoformat(),
        "generator": "Server/tools/data/derive_economy.py",
        "client": {
            "build": CLIENT_BUILD,
            "locale": lang,
            "dataDirectory": str(data_dir),
        },
        "provenance": PROVENANCE,
        "currencies": currencies,
        "rarities": rarities,
        "scrapyard": scrapyard,
        "crates": crates,
        "crateKeys": keys,
        "crateKeyAnomalies": anomalies,
        "bags": bags,
        "wardrobe": wardrobe,
        "matchEnd": match_end,
        "itemSortTypes": sorted(sort_types, key=lambda s: s["displayOrder"]),
        "contentPacks": content_packs,
        "useVerbs": use_verbs,
        "serverSideGaps": gaps,
    }

    doc["counts"] = {
        "currencies": len(currencies),
        "rarities": len(rarities),
        "scrapyard.grinders": len(scrapyard["grinders"]),
        "scrapyard.grinderInputFamilies": len(scrapyard["grinderInputFamilies"]),
        "scrapyard.scrapValues": len(scrapyard["scrapValues"]),
        "scrapyard.currencyPayoutItems": len(scrapyard["currencyPayoutItems"]),
        "scrapyard.itemClassSaleRewardSets": len(scrapyard["itemClassSaleRewardSets"]),
        "scrapyard.storefrontItemIds": len(scrapyard["storefrontItemIds"]),
        "crates": len(crates),
        "crates.reward": sum(1 for c in crates if c["kind"] == "reward"),
        "crates.locked": sum(1 for c in crates if c["kind"] == "locked"),
        "crates.requiringKey": sum(1 for c in crates if c["keyItemId"]),
        "crateKeys": len(keys),
        "crateKeyAnomalies": len(anomalies),
        "bags": len(bags),
        "wardrobe.conversions": len(wardrobe["conversions"]),
        "wardrobe.inputGroups": len(wardrobe["inputGroups"]),
        "wardrobe.sourceLabels": len(wardrobe["sourceLabels"]),
        "wardrobe.skinSlots": len(wardrobe["skinSlots"]),
        "matchEnd.rankBadges": len(match_end["rankBadges"]),
        "matchEnd.panels": len(match_end["panels"]),
        "matchEnd.teamColors": len(match_end["teamColors"]),
        "matchEnd.squadMarkerColors": len(match_end["squadMarkerColors"]),
        "matchEnd.gameModes": len(match_end["gameModes"]),
        "matchEnd.experienceAwards": len(match_end["experienceAwards"]),
        "itemSortTypes": len(sort_types),
        "useVerbs": len(use_verbs),
        "serverSideGaps": len(gaps),
    }
    return doc


#: Header/summary keys that describe the document rather than carry client data.
_UNPROVENANCED_ROOTS = {
    "schema", "generatedAt", "generator", "client", "provenance", "counts", "serverSideGaps",
}


def _leaf_paths(node: Any, path: str, out: set[str], sample: int = 3) -> None:
    """Collect dotted paths to every scalar leaf; list elements collapse onto ``path[]``."""
    if isinstance(node, dict):
        for key, value in node.items():
            _leaf_paths(value, f"{path}.{key}" if path else str(key), out, sample)
    elif isinstance(node, list):
        for element in node[:sample]:
            _leaf_paths(element, path + "[]", out, sample)
    else:
        out.add(path)


def _covered(path: str, keys: set[str]) -> bool:
    """A leaf is documented by its own entry or by any ancestor entry."""
    candidate = path
    while candidate:
        if candidate in keys:
            return True
        if candidate.endswith("[]"):
            candidate = candidate[:-2]
            continue
        if "." not in candidate:
            return False
        candidate = candidate.rsplit(".", 1)[0]
    return False


def selfcheck(doc: dict[str, Any]) -> list[str]:
    """Cheap invariants that would catch a wrong join or a re-extracted input."""
    problems = []

    # Every emitted data field must carry a provenance note.
    leaves: set[str] = set()
    for root, node in doc.items():
        if root in _UNPROVENANCED_ROOTS:
            continue
        _leaf_paths(node, root, leaves)
    undocumented = sorted(p for p in leaves if not _covered(p, set(doc["provenance"])))
    if undocumented:
        problems.append(f"{len(undocumented)} emitted field(s) have no provenance entry: "
                        + ", ".join(undocumented[:10]))

    ids = {c["id"] for c in doc["currencies"]}
    if ids != {1, 4, 5, 6, 7000}:
        problems.append(f"currency ids changed: {sorted(ids)} (expected 1, 4, 5, 6, 7000) - F5")
    by_name = {c["name"]: c["id"] for c in doc["currencies"]}
    if by_name.get("Crowns") != 4 or by_name.get("Scrap") != 1:
        problems.append(f"currency name join broke: {by_name} - F5")
    if any(not c["iconFiles"] for c in doc["currencies"]):
        problems.append("a currency icon no longer resolves through ImageSets/Images - F3")
    grinders = {(g["rarityId"], g["inputCount"]) for g in doc["scrapyard"]["grinders"]}
    if grinders != {(5, 5), (6, 4), (7, 5)}:
        problems.append(f"Grinders.txt changed: {sorted(grinders)} (expected 5x5, 6x4, 7x5) - F8")
    if doc["counts"]["wardrobe.conversions"] != 691:
        problems.append(f"AcctItemConversions row count changed: "
                        f"{doc['counts']['wardrobe.conversions']} (expected 691)")
    if doc["counts"]["matchEnd.rankBadges"] != 201:
        problems.append(f"MatchRankImageSets row count changed: "
                        f"{doc['counts']['matchEnd.rankBadges']} (expected 201) - F11")
    return problems


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="Derive Cranberry's server-ready economy dataset from the August client's "
                    "datasheets.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    ap.add_argument("--data", default=str(DEFAULT_DATA),
                    help="directory holding the extracted datasheets "
                         f"(default {DEFAULT_DATA})")
    ap.add_argument("--out", default=str(DEFAULT_OUT),
                    help=f"output JSON path (default {DEFAULT_OUT})")
    ap.add_argument("--lang", default="en_us", help="locale archive to resolve names with")
    ap.add_argument("--summary", action="store_true",
                    help="print counts and serverSideGaps only; do not write the file")
    ap.add_argument("--indent", type=int, default=1, help="JSON indent (0 for compact)")
    args = ap.parse_args(argv)

    doc = derive(Path(args.data), args.lang)
    problems = selfcheck(doc)

    if not args.summary:
        out = Path(args.out)
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(
            json.dumps(doc, ensure_ascii=False, indent=args.indent or None) + "\n",
            encoding="utf-8", newline="\n",
        )
        print(f"wrote {out} ({out.stat().st_size:,} bytes)", file=sys.stderr)

    print("counts:", file=sys.stderr)
    for k, v in doc["counts"].items():
        print(f"  {k:<42} {v}", file=sys.stderr)
    print("serverSideGaps:", file=sys.stderr)
    for gap in doc["serverSideGaps"]:
        print(f"  {gap['id']}: {gap['need']}", file=sys.stderr)
    if problems:
        print("SELF-CHECK PROBLEMS:", file=sys.stderr)
        for p in problems:
            print(f"  ! {p}", file=sys.stderr)
        return 1
    print("self-check: ok", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
