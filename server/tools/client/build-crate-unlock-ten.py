#!/usr/bin/env python3
"""Patch five stock AVM2 methods for Unlock 10 without recompiling any class.

The stock UI uses unnamed private namespaces in dynamic confirmation lookups.
Source recompilation changes those namespaces and breaks both unlock actions.
This build preserves all traits, constructors, callbacks and preflight methods.
Requires the owner's stock MarketplaceWindow.gfx and local JPEXS 26.2.1/JDK.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET

SOURCE_SHA256 = 'dc8e03de521fdf9a7e5f5f2f24716d897fd1f2a50c24b9cf209895ec1065252c'
CHANGED_METHODS = {981, 987, 1631, 1639, 1858}
CLASSES = 'views.marketplace.managers.MarketplaceManager,views.marketplace.pages.store.AccountDetailPanel,views.marketplace.BuyButton'


def canonical(node):
    # Export-only diagnostic file offsets move when strings/instructions grow;
    # trait bytes and every actual serialized field still must match.
    return node.tag, tuple(sorted((key, value) for key, value in node.attrib.items() if key != 'fileOffset')), node.text if not len(node) else None, tuple(canonical(child) for child in node)


def verify_abc(original_xml, changed_xml):
    original, changed = (ET.parse(path).find('.//abc') for path in (original_xml, changed_xml))
    if original is None or changed is None:
        raise ValueError('Missing ABC structure')
    if [child.tag for child in original] != [child.tag for child in changed]:
        raise ValueError('ABC tables changed')
    for before, after in zip(original, changed, strict=True):
        if before.tag == 'constants':
            if [child.tag for child in before] != [child.tag for child in after]:
                raise ValueError('Constant pool kinds changed')
            for old_pool, new_pool in zip(before, after, strict=True):
                if len(new_pool) < len(old_pool) or any(canonical(a) != canonical(b) for a, b in zip(old_pool, new_pool)):
                    raise ValueError('Existing constant pool entries changed: ' + old_pool.tag)
                appended = list(new_pool)[len(old_pool):]
                if old_pool.tag == 'constant_string':
                    if [entry.text for entry in appended] != ['Unlock 10', 'Unlock ']:
                        raise ValueError('Expected only the two appended button labels')
                elif appended:
                    raise ValueError('Unexpected appended constants: ' + old_pool.tag)
        elif before.tag == 'bodies':
            if len(before) != len(after):
                raise ValueError('Method body count changed')
            actual = set()
            for old_body, new_body in zip(before, after, strict=True):
                if canonical(old_body) == canonical(new_body):
                    continue
                method = int(old_body.attrib['method_info'])
                actual.add(method)
                if method not in CHANGED_METHODS:
                    raise ValueError('Unrelated method body changed: ' + str(method))
                for key in old_body.attrib.keys() | new_body.attrib.keys():
                    if key not in {'codeBytes', 'max_stack'} and old_body.get(key) != new_body.get(key):
                        raise ValueError('Changed method structure: ' + str(method) + ':' + key)
                if tuple(canonical(child) for child in old_body) != tuple(canonical(child) for child in new_body):
                    raise ValueError('Method exceptions/traits changed: ' + str(method))
            if actual != CHANGED_METHODS:
                raise ValueError('Unexpected edited method set: ' + repr(actual))
            unchanged = len(before) - len(actual)
        elif canonical(before) != canonical(after):
            raise ValueError('Unrelated ABC table changed: ' + before.tag)
    return unchanged


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('asset', type=Path)
    parser.add_argument('--ffdec', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    asset, out, ffdec = args.asset.resolve(), args.out.resolve(), args.ffdec.resolve()
    raw = asset.read_bytes()
    if hashlib.sha256(raw).hexdigest() != SOURCE_SHA256:
        raise ValueError('Build requires the exact stock asset; installation supports upgrading the earlier patch separately')
    out.mkdir(parents=True, exist_ok=True)
    destination = out / 'MarketplaceWindow.gfx'
    if destination == asset:
        raise ValueError('Output must differ from source')
    compiled = out / 'compiled.gfx'
    subprocess.run(['java', '-cp', str(ffdec), str(Path(__file__).with_name('CrateUnlockBytecode.java')),
                    str(asset), str(compiled)], check=True)
    spec = importlib.util.spec_from_file_location('gfx_tags', Path(__file__).with_name('build-helmet-click.py'))
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)
    built = helper.preserve_tags(raw, compiled.read_bytes())
    destination.write_bytes(built)
    java = ['java', '-jar', str(ffdec)]
    for source, xml in ((asset, out / 'stock.xml'), (destination, out / 'patched.xml')):
        subprocess.run(java + ['-swf2xml', str(source), str(xml)], check=True)
    unchanged = verify_abc(out / 'stock.xml', out / 'patched.xml')
    subprocess.run(java + ['-selectclass', CLASSES, '-export', 'script', str(out / 'verify'), str(destination)], check=True)
    subprocess.run(java + ['-selectclass', CLASSES, '-format', 'script:pcode', '-export', 'script', str(out / 'pcode'), str(destination)], check=True)
    subprocess.run(['node', str(Path(__file__).with_name('verify-crate-unlock-ten.cjs')), str(out / 'verify' / 'scripts')], check=True)
    manifest = {
        'asset': 'MarketplaceWindow.gfx', 'pack': 'Assets_164.pack',
        'source_sha256': SOURCE_SHA256, 'output_sha256': hashlib.sha256(built).hexdigest(),
        'output': str(destination), 'changed_method_info': sorted(CHANGED_METHODS),
        'unchanged_method_bodies': unchanged,
        'all_original_constants_traits_namespaces_signatures_and_initializers_preserved': True,
        'one_changed_doabc': True, 'other_tags_and_trailer_preserved': True,
        'batch_quantity': 'min(10, owned); stock inventory supplies positive owned counts',
    }
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print(json.dumps(manifest, indent=2))


if __name__ == '__main__':
    main()
