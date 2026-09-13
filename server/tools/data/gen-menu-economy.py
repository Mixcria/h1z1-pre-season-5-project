#!/usr/bin/env python3
"""Generate the menu account economy, preserving source IDs, scrap values and provenance.

Known crate memberships/weights are adopted data, not claimed retail probabilities.
Nomad, Nemesis, Frostbite and Legacy memberships use the cached container reference.
Unresolved crate families are recorded rather than assigned another family's rewards.
"""
import argparse
import csv
import hashlib
import html
import json
import re
from decimal import Decimal
from pathlib import Path
from locked_special_crates import locked_rows


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def sheet(path):
    with path.open(encoding='utf-8-sig') as source:
        return list(csv.DictReader(source.read().lstrip('#*').splitlines(), delimiter='^'))


def quoted(value):
    return json.dumps(str(value), ensure_ascii=True)


def generate(args):
    repo = Path(__file__).resolve().parents[2]
    data = Path(args.data)
    source = Path(args.crates)
    containers_path = Path(args.containers)
    economy_path = data / 'derived/economy.json'
    names_path = data / 'item-names-en_us.json'
    items_path = data / 'ClientItemDefinitions.txt'
    catalog_path = repo / 'src/Cranberry.Zone/AugustSkinCatalog.g.cs'
    vehicles_path = repo / 'src/Cranberry.Zone/Vehicles/VehicleSkinCatalog.cs'
    economy = json.loads(economy_path.read_text(encoding='utf-8-sig'))
    names = json.loads(names_path.read_text(encoding='utf-8-sig'))
    items = {int(x['ID']): x for x in sheet(items_path)}
    owned = {int(account): int(reward) for _, reward, account in re.findall(
        r'new\((\d+), (\d+), (\d+), \d+, "', catalog_path.read_text(encoding='utf-8-sig'))}
    owned.update({int(item): int(item) for _, _, item in re.findall(
        r'new\((\d+), (\d+), (\d+), \d+\)', vehicles_path.read_text(encoding='utf-8-sig'))})
    # Emotes are native account items themselves, not AcctItemConversions-backed
    # apparel. Keep their real IDs so the native emote inventory sees ownership.
    emotes = {item for item, row in items.items() if row['CODE_FACTORY_NAME'] == 'EmoteAnimation'}
    owned.update({item: item for item in emotes})
    # AccountGiveRewardSet entries are one draw awarding both named cosmetics.
    # Client names/IDs identify the constituent bandana/shades or boots/gloves.
    bundles = {
        2326: (2305, 2296), 2327: (2303, 2298), 2328: (2301, 2293),
        2329: (2302, 2294), 2330: (2307, 2297), 2331: (2304, 2295),
        2332: (2306, 2292), 2724: (2680, 2670), 2725: (2679, 2669),
    }
    # The reference uses the duplicate AccountRecipe ID; the native conversion
    # table's supported Blue Camo Pants ownership is 1821 -> 2064.
    aliases = {2497: 1821}
    for bundle, members in bundles.items():
        if items[bundle]['CODE_FACTORY_NAME'] != 'AccountGiveRewardSet' or any(item not in owned for item in members):
            raise ValueError(f'Invalid native reward bundle mapping: {bundle}')
    by_reward = {}
    for account, reward in owned.items():
        by_reward.setdefault(reward, []).append(account)
    def resolve(item):
        item = aliases.get(item, item)
        if item in owned:
            return item
        candidates = by_reward.get(item, [])
        return candidates[0] if len(candidates) == 1 else None
    scrap = {x['itemId']: x for x in economy['scrapyard']['scrapValues']}
    skins = []
    for account, reward in sorted(owned.items()):
        row = items[account]
        value = int(row['SCRAP_VALUE_OVERRIDE'])
        skins.append((account, reward, names.get(str(account), {}).get('name') or f'Account item {account}',
                      int(row['RARITY']), value, value > 0 and scrap.get(account, {}).get('scrappableByClient', False)))
    rarity = {row[0]: row[3] for row in skins}
    design_weights = {0: 60, 5: 60, 6: 25, 7: 10, 8: 5}
    excluded = {}
    def reward_rows(rows, design=False, source_id=0):
        result = {}
        for row in rows:
            raw = int(row['itemDefinitionId'])
            members = bundles.get(raw, (raw,))
            account = resolve(members[0])
            if account is None:
                excluded.setdefault(source_id, []).append(raw)
                continue
            weight = design_weights[rarity[account]] if design else int(Decimal(str(row['rewardChance'])) * 1000)
            if weight <= 0:
                raise ValueError(f'Nonpositive reward weight: {raw}')
            extra = tuple((resolve(member), owned[resolve(member)], 1) for member in members[1:])
            result[account] = (account, owned[account], 1, weight, extra, raw)
        return sorted(result.values())
    pools = {int(x['itemDefinitionId']): reward_rows(x['rewards'], source_id=int(x['itemDefinitionId']))
             for x in json.loads(source.read_text(encoding='utf-8-sig'))
             if int(x['itemDefinitionId']) in items}
    reward_crates = {x['itemId']: x for x in economy['crates'] if x['kind'] == 'reward'}
    by_set = {}
    for item, pool in pools.items():
        if item in reward_crates and pool:
            by_set[reward_crates[item]['rewardSetId']] = pool
    # Elite bags and Elite crates refer to the same August reward set.
    for bag in economy['bags']:
        if bag['itemId'] in pools and pools[bag['itemId']]:
            by_set[bag['rewardSetId']] = pools[bag['itemId']]
    # Cached reference membership, joined to exact August owned names. The site renamed
    # the Nautilus shotgun; its /nautilus-12ga-pump-shotgun URL preserves the August name.
    containers = containers_path.read_text(encoding='utf-8-sig')
    start = containers.index('<div class="cont-wrap" data-contid="3638">')
    end = containers.find('<div class="cont-wrap"', start + 10)
    nemesis_names = re.findall(r'class="sritemsmall_inner" title="([^"]+)"', containers[start:end])
    if len(nemesis_names) != 23 or 'Nemesis Crate' not in containers[start:end]:
        raise ValueError('Cached Nemesis reference block changed; review membership before generating')
    nemesis_items = []
    reference_name_aliases = {'Nautilus Riot Shotgun': 'Nautilus 12GA Pump Shotgun'}
    for label in nemesis_names:
        label = html.unescape(label).strip()
        label = reference_name_aliases.get(label, label)
        matches = [account for account in owned
                   if names.get(str(account), {}).get('name', '').casefold() == label.casefold()]
        if len(matches) == 1:
            nemesis_items.append({'itemDefinitionId': matches[0]})
        else:
            raise ValueError(f'Nemesis membership does not resolve unambiguously: {label}: {matches}')
    by_set[5454] = reward_rows(nemesis_items, design=True, source_id=4161)
    # Retired Frostbite and Legacy memberships are in the same cached reference as
    # Nemesis. Resolve names only against supported August account ownership.
    reference_sets = {5372: 'Frostbite', 5379: 'Legacy', 5386: 'Nomad'}
    reference_aliases = {
        'Scavenger Sniper Rifle': 'Scavenger .308 Hunting Rifle',
        'Bandit Magnum': 'Bandit .44 Magnum',
        'Four Alarm Riot Shotgun': 'Four Alarm 12GA Pump Shotgun',
        'Four Alarm Magnum': 'Four Alarm .44 Magnum',
        'Toxic Riot Shotgun': 'Toxic Shotgun',
        'Blue Camo Tactical Helmet w-Goggles': 'Blue Camo Tactical Helmet w/Goggles',
    }
    for cont_id, set_id, expected in [('3501', 5372, 26), ('3527', 5379, 19), ('3566', 5386, 23)]:
        block = containers.split('<div class="cont-wrap" data-contid="' + cont_id + '">')[1].split('<div class="cont-wrap"')[0]
        labels = re.findall(r'class="sritemsmall_inner" title="([^"]+)"', block)
        if len(labels) != expected or reference_sets[set_id] + ' Crate' not in block:
            raise ValueError('Cached retired crate reference changed')
        members = []
        for label in labels:
            label = html.unescape(label).strip()
            label = reference_aliases.get(label, label)
            matches = [account for account in owned
                       if names.get(str(account), {}).get('name', '').casefold() == label.casefold()]
            if len(matches) == 1:
                members.append({'itemDefinitionId': matches[0]})
            else:
                raise ValueError(f'Retired crate member does not resolve: {label}: {matches}')
        by_set[set_id] = reward_rows(members, design=True, source_id=set_id)
    designed_sets = set()
    # Client tiers are distinct pools; match their minimum rarity to the same family.
    for root_id, tier_sets in [(5271, [5272, 5273, 5274]), (5386, [5387, 5388, 5389]),
                              (5454, [5455, 5456, 5457]), (5372, [5373, 5374, 5375])]:
        for minimum, set_id in zip([6, 7, 8], tier_sets):
            if set_id not in by_set:
                by_set[set_id] = [r for r in by_set[root_id] if rarity[r[0]] >= minimum]
                designed_sets.add(set_id)
    crates = []
    unresolved = []
    for row in economy['crates']:
        target = row['unlockedCrateItemId'] or row['itemId']
        if row['keyItemId']:
            # Old keyed crates and their modern locked counterparts unlock the same free item.
            candidates = [x for x in reward_crates.values()
                          if x['rewardSetId'] == row['rewardSetId'] and x['keyItemId'] is None
                          and x['name'].endswith(' - Unlocked')]
            if len(candidates) == 1:
                target = candidates[0]['itemId']
        set_id = reward_crates[target]['rewardSetId']
        if not by_set.get(set_id):
            unresolved.append((row['itemId'], row['name'], set_id))
            continue
        # Type 37 exposes the native Crown unlock controls. Legacy type 45 instead
        # sends f503 (Open); PARAM2 is its real key item, not a purchase bundle.
        cost = args.crowns if row['kind'] == 'locked' else 0
        provenance = ('ADOPTED cached SurvivorsRest Nomad membership; DESIGN rarity weights' if set_id in range(5386, 5390)
                      else 'ADOPTED cached SurvivorsRest Nemesis membership; DESIGN rarity weights' if set_id in range(5454, 5458)
                      else 'ADOPTED cached SurvivorsRest Frostbite membership; DESIGN rarity weights' if set_id in range(5372, 5376)
                      else 'ADOPTED cached SurvivorsRest Legacy membership; DESIGN rarity weights' if set_id == 5379
                      else 'ADOPTED AccountCrates.json membership and weights, restricted to supported August ownership')
        if set_id in designed_sets:
            provenance += '; DESIGN tier minimum rarity'
        provenance += f'; DESIGN locked opening price {args.crowns} Crowns; CLIENT legacy key {row["keyItemId"] or 0}; unlocked opening free'
        crates.append((row['itemId'], row['name'], set_id, target, cost, by_set[set_id], provenance,
                       row['backendBundleId'] or 0, row['keyItemId'] or 0))
    # Special/reward-only families get authored locked wrappers in the client
    # datasheet too. Paid unlocks grant the unchanged native reward crate.
    for row in locked_rows(items):
        item, target = int(row['ID']), int(row['PARAM1'])
        original = next(crate for crate in crates if crate[0] == target)
        name = original[1].removesuffix(' - Unlocked')
        crates.append((item, name, original[2], target, args.crowns, original[5],
                       original[6] + '; DESIGN private locked wrapper; requires matching client definitions',
                       int(row['PARAM2']), 0))
        items[item] = row
    scrap_pool = reward_rows([{'itemDefinitionId': item} for item in economy['scrapyard']['storefrontItemIds']
                              if item not in emotes], design=True)
    if excluded:
        raise ValueError(f'Native reward items must not be silently excluded: {excluded}')
    if not scrap_pool:
        raise ValueError('No valid August Scrap rewards')
    inputs = [economy_path, names_path, items_path, catalog_path, vehicles_path, source, containers_path,
              Path(__file__).with_name('locked_special_crates.py')]
    def reward_code(reward):
        account, appearance, count, weight, extra, original = reward
        fields = ', '.join(f'{n}u' for n in (account, appearance, count, weight))
        companions = '[' + ', '.join('new(' + ', '.join(f'{n}u' for n in item) + ')' for item in extra) + ']'
        return 'new(' + fields + ', ' + companions + f', {original}u),'
    lines = ['// <auto-generated>', '// tools/data/gen-menu-economy.py. August item IDs and scrap values;',
             '// adopted source weights are not verified retail odds. DESIGN prices are configurable at generation.',
             '// generator-sha256: ' + digest(Path(__file__))]
    lines += [f'// input: {path.as_posix()} sha256={digest(path)}' for path in inputs]
    lines += ['// Excluded reference reward IDs lacking an unambiguous supported August ownership mapping: ' + repr(excluded),
              '// Unresolved (not enabled; no invented reward membership): ' + repr(unresolved),
              '// </auto-generated>', '#nullable enable', 'namespace Cranberry.Zone.Economy;',
              'internal static class EconomyCatalogData', '{', '    internal static EconomyCatalog Create() => new(',
              '        skins:', '        [']
    for account, reward, name, rarity_id, value, can_scrap in skins:
        row = items[account]
        lines.append(f'            new({account}u, {reward}u, {quoted(name)}, {rarity_id}u, {value}, {str(can_scrap).lower()}, {row["NAME_ID"]}u, {row["IMAGE_SET_ID"]}u),')
    lines += ['        ],', '        crates:', '        [']
    for item, name, set_id, target, cost, pool, provenance, bundle_id, key_id in crates:
        lines.append(f'            new({item}u, {quoted(name)}, {set_id}u, {target}u, {cost}u,')
        lines.append('            [')
        lines += ['                ' + reward_code(reward) for reward in pool]
        row = items[item]
        lines += ['            ], ' + quoted(provenance) + f', {bundle_id}u, {row["NAME_ID"]}u, {row["IMAGE_SET_ID"]}u, {row["RARITY"]}u, {key_id}u),']
    lines += ['        ],', '        scrapyardRewards:', '        [']
    lines += ['            ' + reward_code(reward) for reward in scrap_pool]
    lines += ['        ],', f'        scrapyardCost: {args.scrap}u,',
              f'        scrapyardProvenance: "CLIENT ItemSourceLookup Available in the Scrapyard; DESIGN rarity weights 60/25/10/5 per item; DESIGN {args.scrap} Scrap per roll");', '}']
    output = Path(args.out) if args.out else repo / 'src/Cranberry.Zone/Economy/EconomyCatalogData.g.cs'
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text('\n'.join(lines) + '\n', encoding='utf-8')
    print(json.dumps({'skins': len(skins), 'crates': len(crates), 'scrapyardRewards': len(scrap_pool),
                      'unresolved': unresolved, 'output': str(output)}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--data', default=r'C:\Aug2017\out\data_aug')
    parser.add_argument('--crates', default=r'C:\h1z1project\reference\h1z1-server\data\2016\dataSources\AccountCrates.json')
    parser.add_argument('--containers', default=r'C:\Project\out\wave20260823-r41\survivorsrest\containers.html')
    parser.add_argument('--crowns', type=int, default=250)
    parser.add_argument('--scrap', type=int, default=100)
    parser.add_argument('--out')
    args = parser.parse_args()
    if not 0 < args.crowns <= 2147483647 or not 0 < args.scrap <= 2147483647:
        parser.error('Costs must be positive signed 32-bit amounts')
    generate(args)
