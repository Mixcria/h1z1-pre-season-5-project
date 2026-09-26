"""Cranberry's loadout-17 grenade slot, shared by client and server generators.

The newer installed client places throwables in wire slot 9 / input Slot4,
binoculars in 5 / Slot5 and fists in 7 / Slot6. Keep the August schema and
required empty-hand item. August locale 14624/14638 replaces newer-only 18940.
"""
from copy import deepcopy


def apply_layout(name, source):
    rows = deepcopy(source)
    if name == 'LoadoutSlots.txt':
        def one(slot):
            found = [r for r in rows if r['LOADOUT_ID'] == '17' and r['SLOT_ID'] == slot]
            if len(found) != 1:
                raise ValueError(f'Expected one survivor slot {slot}')
            return found[0]
        fists = one('7')
        if fists['ITEM_ID'] != '85' or fists['FLAG_REQUIRED'] != '1':
            raise ValueError('August required fists definition changed')
        fists.update(SLOT_INPUT_ACTION='Slot6', FLAG_CAN_CUSTOMIZE='0',
                     FLAG_IS_VISIBLE='0', DISPLAY_INDEX='0')
        grenade = dict(one('1'))
        grenade.update(SLOT_ID='9', NAME_ID='14624', DESCRIPTION_ID='14638',
                       ICON_ID='1094', SLOT_INPUT_ACTION='Slot4', DISPLAY_INDEX='10')
        existing = [r for r in rows if r['LOADOUT_ID'] == '17' and r['SLOT_ID'] == '9']
        if existing:
            if existing != [grenade]:
                raise ValueError('Conflicting survivor grenade slot')
        else:
            rows.insert(rows.index(fists) + 1, grenade)
    elif name == 'LoadoutSlotItemClasses.txt':
        rows = [r for r in rows if not (r['LOADOUT_ID'] == '17'
                and r['SLOT'] in ('1', '2', '4') and r['ITEM_CLASS'] == '25078')]
        grenade = dict(LOADOUT_ID='17', SLOT='9', ITEM_CLASS='25078', FLAG_LOCKED='0')
        existing = [r for r in rows if r['LOADOUT_ID'] == '17' and r['SLOT'] == '9']
        if existing:
            if existing != [grenade]:
                raise ValueError('Conflicting survivor grenade class')
        else:
            index = max(i for i, r in enumerate(rows)
                        if r['LOADOUT_ID'] == '17' and int(r['SLOT']) < 9)
            rows.insert(index + 1, grenade)
    else:
        raise ValueError('Not a loadout sheet: ' + name)
    return rows
