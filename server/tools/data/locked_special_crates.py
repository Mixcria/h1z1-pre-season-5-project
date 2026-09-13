"""Shared client/server definitions for Crown-locked special August crates.

The original reward items remain the unlocked results of a paid purchase.
New IDs are explicit private-server data; no existing native definition changes.
"""
import csv

SPECIAL_CRATES = tuple(range(3807, 3819)) + (3926,)
LOCKED_ID_OFFSET = 10000
LEGACY_NAME_ID = 60000


def locked_rows(items):
    for target in SPECIAL_CRATES:
        item_id = LOCKED_ID_OFFSET + target
        if item_id in items:
            raise ValueError(f'Private locked item ID collision: {item_id}')
        source = items[target]
        if source['CODE_FACTORY_NAME'] != 'RewardCrate' or source['PARAM2'] != '0':
            raise ValueError(f'Expected an unkeyed special reward crate: {target}')
        row = dict(items[3620])  # Proven native LockedRewardCrate behavior/flags.
        for field in ('NAME_ID', 'IMAGE_SET_ID', 'RARITY'):
            row[field] = source[field]
        row.update(ID=str(item_id), PARAM1=str(target), PARAM2=str(item_id))
        if target == 3926:
            row['NAME_ID'] = str(LEGACY_NAME_ID)
        yield row


def extend_client_sheet(original):
    text = original.decode('utf-8-sig')
    reader = csv.DictReader(text.lstrip('#*').splitlines(), delimiter='^')
    items = {int(row['ID']): row for row in reader}
    new = list(locked_rows(items))
    newline = b'\r\n' if b'\r\n' in original else b'\n'
    if not original.endswith(newline):
        raise ValueError('Expected newline-terminated native definitions')
    appended = newline.join('^'.join(row[field] for field in reader.fieldnames).encode('utf-8') for row in new)
    return original + appended + newline
