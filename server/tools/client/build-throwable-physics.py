#!/usr/bin/env python3
"""Build two content-addressed throwable pack updates from pinned August/retail assets.

Does not install or publish. Every sibling asset and unrelated manifest entry is preserved.
The retail extractor is tools/pack/retail_vehicle_assets.py; see the deployment provenance.
"""
import argparse
import copy
import hashlib
import importlib.util
import json
import re
from pathlib import Path
import xml.etree.ElementTree as ET

PINS = {
    'PhysicsMaterials.apx': (
        '07c3bd25d505da8f6bda9d8b35f72e1179bc9b2c6bc2f7f032017e3425ec9df7',
        '1746fc5783ab3e63ff29dd63ca42f83eb201795467073da828accb8e253f3d9e'),
    'Weapons_Grenades_HEGrenade_OnGround_COL.apx': (
        'c8544fb0c47e938df74e5cba7a5c4386b24e6b4c927d0d563badf7591dbb290a',
        '90a6603b72d0ad1f665d34cd8be3a30c455758accfae139e8c2ab5b73419b640'),
}


def sha(data):
    return hashlib.sha256(data).hexdigest()


def grenade_material(august, retail):
    if (sha(august), sha(retail)) != PINS['PhysicsMaterials.apx']:
        raise ValueError('Physics material input is not the reviewed August/retail build')
    pattern = rb'(?ms)^      <value type="Ref".*?^      </value>\r?\n'
    old_rows, retail_rows = re.findall(pattern, august), re.findall(pattern, retail)
    assert len(old_rows) == 49 and len(retail_rows) == 50
    selected = [row for row in retail_rows if b'>Grenades</value>' in row]
    assert len(selected) == 1 and not any(b'>Grenades</value>' in row for row in old_rows)
    array = ET.fromstring(august).find('.//array')
    assert array is not None and array.attrib['size'] == '49'
    marker = b'size="49"'
    assert august.count(marker) == 1 and august.count(b'    </array>') == 1
    changed = august.replace(marker, b'size="50"').replace(b'    </array>', selected[0] + b'    </array>')
    assert re.findall(pattern, changed)[:-1] == old_rows
    assert changed.replace(selected[0], b'').replace(b'size="50"', marker) == august
    rows = ET.fromstring(changed).findall('.//value[@className="PhysicsMaterial"]')
    assert len(rows) == 50
    values = {v.attrib['name']: v.text for v in rows[-1].find('struct').findall('value')}
    assert values['Name'] == 'Grenades'
    assert values['DynamicFriction'] == values['StaticFriction'] == '1.5'
    assert values['FrictionCombineMode'] == values['RestitutionCombineMode'] == 'MAX'
    return changed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('august-assets', 'retail-assets', 'manifest', 'client', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    manifest_bytes = args.manifest.read_bytes()
    original = json.loads(manifest_bytes)
    spec = importlib.util.spec_from_file_location('pack_update', Path(__file__).with_name('install-bounty-lobby.py'))
    pack = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(pack)
    patches = {}
    for asset, (old_pin, retail_pin) in PINS.items():
        old, retail = (args.august_assets / asset).read_bytes(), (args.retail_assets / asset).read_bytes()
        assert sha(old) == old_pin and sha(retail) == retail_pin, asset
        patches[asset] = grenade_material(old, retail) if asset == 'PhysicsMaterials.apx' else retail
    # Same August parameter classes/version; no new engine feature or cooked binary format.
    collision = ET.fromstring(patches['Weapons_Grenades_HEGrenade_OnGround_COL.apx'])
    exe = (args.client / 'H1Z1.exe').read_bytes()
    for value in collision.iter('value'):
        if 'className' in value.attrib:
            assert value.attrib['version'] == '0.0'
            assert value.attrib['className'].encode() + b'\0' in exe
    assert collision.find('.//value[@name="PhysicsMaterialName"]').text == 'Grenades'
    result = copy.deepcopy(original)
    result['buildId'] = 'aug2017-throwable-physics-20260913'
    args.output.mkdir(parents=True, exist_ok=False)
    content = args.output / 'content'
    content.mkdir()
    changes = []
    for asset, pack_name in [('PhysicsMaterials.apx', 'Assets_252.pack'),
                             ('Weapons_Grenades_HEGrenade_OnGround_COL.apx', 'Assets_050.pack')]:
        rel = 'Resources/Assets/' + pack_name
        row = next(r for r in result['files'] if r['path'] == rel)
        before = dict(row)
        data = (args.client / rel).read_bytes()
        assert sha(data).upper() == row['sha256'] and len(data) == row['size'], rel
        pack.NAME = asset
        updated, index, siblings = pack.prepare_update(data, patches[asset], PINS[asset][0], sha(patches[asset]))
        row.update(size=len(updated), sha256=sha(updated).upper())
        (content / row['sha256']).write_bytes(updated)
        changes.append(dict(asset=asset, before=before, after=dict(row), otherAssetsUnchanged=siblings,
                            indexOffset=index, assetSha256=sha(patches[asset]), retailSha256=PINS[asset][1]))
    allowed = {r['before']['path'] for r in changes}
    assert len(result['files']) == len(original['files']) == 853
    assert [r for r in original['files'] if r['path'] not in allowed] == [r for r in result['files'] if r['path'] not in allowed]
    (args.output / 'manifest.json').write_text(json.dumps(result, indent=2) + '\n')
    report = dict(baselineManifestSha256=sha(manifest_bytes),
                  manifestSha256=sha((args.output / 'manifest.json').read_bytes()),
                  unchangedManifestEntries=851, changes=changes)
    (args.output / 'physics-audit.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
