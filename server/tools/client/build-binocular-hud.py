#!/usr/bin/env python3
"""Build the verified ammo-visibility GFx patch from the stock August asset."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET

ASSETS = {
    'HudPlayerResourcesWindow.gfx': ('c9dabbb639cbe7124ad9803864fe9b7af92467786678937d5993b6136f16bacb', 'ammo', 'ui.datasource.rows.CurrentLoadoutRow'),
}


def digest(data):
    return hashlib.sha256(data).hexdigest()


def canonical(node):
    return (node.tag, tuple(sorted((key, value) for key, value in node.attrib.items() if key != 'fileOffset')),
            node.text if not len(node) else None, tuple(canonical(child) for child in node))


def verify_abc(original_xml, changed_xml, changed_method):
    original, changed = (ET.parse(path).find('.//abc') for path in (original_xml, changed_xml))
    if original is None or changed is None or [x.tag for x in original] != [x.tag for x in changed]:
        raise ValueError('ABC structure changed')
    edited = []
    for before, after in zip(original, changed, strict=True):
        if before.tag == 'constants':
            for old, new in zip(before, after, strict=True):
                if len(new) < len(old) or any(canonical(a) != canonical(b) for a, b in zip(old, new)):
                    raise ValueError('Existing constants changed')
                if len(new) != len(old) and old.tag != 'constant_string':
                    raise ValueError('Only appended string constants are permitted')
        elif before.tag == 'bodies':
            for old, new in zip(before, after, strict=True):
                if canonical(old) == canonical(new):
                    continue
                edited.append(int(old.attrib['method_info']))
                if any(old.get(key) != new.get(key) for key in old.attrib.keys() | new.attrib.keys()
                       if key not in {'codeBytes', 'max_stack'}):
                    raise ValueError('Method metadata changed')
                if tuple(map(canonical, old)) != tuple(map(canonical, new)):
                    raise ValueError('Method exceptions or traits changed')
        elif canonical(before) != canonical(after):
            raise ValueError('Unrelated ABC table changed: ' + before.tag)
    if edited != [changed_method]:
        raise ValueError('Unexpected changed method set: ' + repr(edited))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--assets', type=Path, required=True)
    parser.add_argument('--ffdec', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    spec = importlib.util.spec_from_file_location('gfx_tags', Path(__file__).with_name('build-helmet-click.py'))
    gfx = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(gfx)
    args.out.mkdir(parents=True, exist_ok=True)
    java = ['java', '-jar', str(args.ffdec.resolve())]
    manifest = {}
    for name, (expected, mode, cls) in ASSETS.items():
        source = (args.assets / name).resolve()
        raw = source.read_bytes()
        if digest(raw) != expected:
            raise ValueError('Unrecognized stock asset: ' + name)
        work = args.out / mode
        work.mkdir(exist_ok=True)
        edited = work / name
        run = subprocess.run(['java', '-cp', str(args.ffdec.resolve()),
                              str(Path(__file__).with_name('BinocularHudBytecode.java').resolve()),
                              str(source), str(edited.resolve()), mode], check=True, text=True, capture_output=True)
        print(run.stdout)
        method = int(re.search(r'CHANGED_METHOD=(\d+)', run.stdout)[1])
        final = args.out / name
        final.write_bytes(gfx.preserve_tags(raw, edited.read_bytes()))
        for asset, xml in ((source, work / 'stock.xml'), (final, work / 'patch.xml')):
            subprocess.run(java + ['-swf2xml', str(asset), str(xml)], check=True, capture_output=True)
        verify_abc(work / 'stock.xml', work / 'patch.xml', method)
        subprocess.run(java + ['-selectclass', cls, '-export', 'script', str(work / 'verify'), str(final)],
                       check=True, capture_output=True)
        manifest[name] = {'source_sha256': expected, 'patched_sha256': digest(final.read_bytes()),
                          'changed_method': method, 'only_one_method_changed': True,
                          'unrelated_tags_preserved': True}
    subprocess.run(['node', str(Path(__file__).with_name('verify-binocular-hud.cjs')),
                    str(args.out.resolve())], check=True)
    (args.out / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print(json.dumps(manifest, indent=2))


if __name__ == '__main__':
    main()
