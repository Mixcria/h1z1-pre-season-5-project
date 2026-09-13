#!/usr/bin/env python3
"""Repair lobby invitation activation/delivery on the currently installed August UI.

Build and verify only. Preserve avatars, overlay, menu changes and all non-script
tags. Install with install-ingame-social.py using this build's guarded manifest.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess

HERE = Path(__file__).resolve().parent


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, HERE / filename)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


social = module('invite_social', 'build-ingame-social.py')
gfx = module('invite_gfx', 'build-helmet-click.py')


def patch(source):
    if 'function LocalShowInvite(' in source:
        raise ValueError('Invitation delivery repair is already present')
    additions = (HERE / 'social-lobby-methods.as').read_text(encoding='utf-8')
    for name in ['processRightClickMenu', 'LocalShowInvite']:
        a, b = social.method_span(additions, name)
        if 'function ' + name + '(' in source:
            c, d = social.method_span(source, name)
            source = source[:c] + additions[a:b] + source[d:]
        else:
            at = source.rfind('   }')
            source = source[:at] + additions[a:b] + '\n' + source[at:]
    a, b = social.method_span(source, 'LocalReceive')
    receive = source[a:b]
    start = receive.index('         if(invite != "" && invite != m_localInvite)')
    end = receive.rindex('         return true;')
    receive = receive[:start] + receive[end:]
    receive = social.replace(receive, '         m_localFriends.sortOn(',
        '         LocalShowInvite(invite,inviter);\n         m_localFriends.sortOn(')
    source = source[:a] + receive + source[b:]
    return social.replace(source, '         if(!row.enabled)', '         if(row == null || !row.enabled)')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--asset', type=Path, required=True)
    parser.add_argument('--ffdec', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    original = args.asset.read_bytes()
    with (out / 'compiler.log').open('w', encoding='utf-8') as log:
        def run(arguments):
            subprocess.run(['java', '-jar', str(args.ffdec.resolve())] + arguments,
                stdout=log, stderr=subprocess.STDOUT, check=True)
        run(['-export', 'script', str(out / 'before'), str(args.asset.resolve())])
        target = out / 'patch' / social.LOBBY
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(patch((out / 'before/scripts' / social.LOBBY).read_text(encoding='utf-8')),
            encoding='utf-8', newline='\n')
        run(['-onerror', 'abort', '-importScript', str(args.asset.resolve()), str(out / 'compiled.gfx'), str(out / 'patch')])
        built = gfx.preserve_tags(original, (out / 'compiled.gfx').read_bytes())
        candidate = out / 'UIRoot.gfx'
        candidate.write_bytes(built)
        run(['-export', 'script', str(out / 'after'), str(candidate)])
    before, after = out / 'before/scripts', out / 'after/scripts'
    originals = {p.relative_to(before).as_posix(): p for p in before.rglob('*.as')}
    candidates = {p.relative_to(after).as_posix(): p for p in after.rglob('*.as')}
    if originals.keys() != candidates.keys():
        raise ValueError('Class inventory changed')
    changed = [name for name in originals if originals[name].read_bytes() != candidates[name].read_bytes()]
    if changed != [social.LOBBY]:
        raise ValueError('Unexpected changed classes: ' + str(changed))
    social.verify_unrelated_methods(originals[social.LOBBY].read_text(encoding='utf-8'),
        candidates[social.LOBBY].read_text(encoding='utf-8'),
        {'LocalReceive', 'processRightClickMenu', 'handleRightClickMenuIndexChange'})
    subprocess.run(['node', str(HERE / 'verify-social-overlay.cjs'), str(after)], check=True)
    manifest = {'asset': 'UIRoot.gfx', 'pack': 'Assets_060.pack',
        'source_sha256': hashlib.sha256(original).hexdigest(), 'output_sha256': hashlib.sha256(built).hexdigest(),
        'output': str(candidate), 'changed_classes': changed, 'unchanged_classes': len(originals) - 1,
        'non_script_tags_preserved': True, 'unrelated_methods_preserved': True, 'handler_tests_passed': True}
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    print(json.dumps(manifest, indent=2))


if __name__ == '__main__':
    main()
