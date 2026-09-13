#!/usr/bin/env python3
"""Build and verify lobby sound/overlay invitation controls from the current installed pack."""
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


social = module('notification_social', 'build-ingame-social.py')
gfx = module('notification_gfx', 'build-helmet-click.py')
packs = module('notification_packs', 'install-bounty-lobby.py')
repair = module('notification_delivery', 'build-party-invite-delivery.py')


def update_method(source, additions, name):
    a, b = social.method_span(additions, name)
    if 'function ' + name + '(' in source:
        c, d = social.method_span(source, name)
        return source[:c] + additions[a:b] + source[d:]
    # Console scripts can include package-level helper classes after the manager.
    at = source.index('\n   }\n}')
    return source[:at] + additions[a:b] + '\n' + source[at:]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pack', type=Path, required=True)
    parser.add_argument('--ffdec', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    pack = args.pack.read_bytes()
    entry, = [e for e in packs.entries(pack) if e[0] == 'UIRoot.gfx']
    original = pack[entry[2]:entry[2] + entry[3]]
    asset = out / 'original.gfx'
    asset.write_bytes(original)
    with (out / 'compiler.log').open('w', encoding='utf-8') as log:
        def run(arguments):
            subprocess.run(['java', '-jar', str(args.ffdec.resolve())] + arguments,
                           stdout=log, stderr=subprocess.STDOUT, check=True)
        run(['-export', 'script', str(out / 'before'), str(asset)])
        before = out / 'before/scripts'
        for filename, additions_file, methods in [
            (social.CONSOLE, 'overlay-lobby-invite-methods.as', ['OverlayCloseForInvite', 'overlayInviteFriend', 'overlayBuild', 'overlayReceive']),
            (social.LOBBY, 'lobby-notification-methods.as', ['LocalShowInvite'])
        ]:
            source = (before / filename).read_text(encoding='utf-8')
            if filename == social.LOBBY and 'function LocalShowInvite(' not in source:
                source = repair.patch(source)
            if filename == social.LOBBY and 'private static var m_localAlertedInvite:String' not in source:
                marker = '      private static var m_localInvite:String = "";'
                source = social.replace(source, marker, marker + '\n      private static var m_localAlertedInvite:String = "";')
            additions = (HERE / additions_file).read_text(encoding='utf-8')
            for name in methods:
                source = update_method(source, additions, name)
            target = out / 'patch' / filename
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(source, encoding='utf-8', newline='\n')
        run(['-onerror', 'abort', '-importScript', str(asset), str(out / 'compiled.gfx'), str(out / 'patch')])
        built = gfx.preserve_tags(original, (out / 'compiled.gfx').read_bytes())
        candidate = out / 'UIRoot.gfx'
        candidate.write_bytes(built)
        run(['-export', 'script', str(out / 'after'), str(candidate)])
    after = out / 'after/scripts'
    originals = {p.relative_to(before).as_posix(): p for p in before.rglob('*.as')}
    candidates = {p.relative_to(after).as_posix(): p for p in after.rglob('*.as')}
    if originals.keys() != candidates.keys():
        raise ValueError('Class inventory changed')
    changed = [name for name in originals if originals[name].read_bytes() != candidates[name].read_bytes()]
    if not changed or set(changed) - {social.CONSOLE, social.LOBBY}:
        raise ValueError('Unexpected changed classes: ' + str(changed))
    for filename, methods in [(social.CONSOLE, {'overlayBuild', 'overlayReceive'}),
                              (social.LOBBY, {'LocalShowInvite', 'LocalReceive', 'processRightClickMenu', 'handleRightClickMenuIndexChange'})]:
        social.verify_unrelated_methods(originals[filename].read_text(encoding='utf-8'),
                                        candidates[filename].read_text(encoding='utf-8'), methods)
    subprocess.run(['node', str(HERE / 'verify-social-overlay.cjs'), str(after)], check=True)
    manifest = {'asset': 'UIRoot.gfx', 'pack': 'Assets_060.pack',
                'source_sha256': hashlib.sha256(original).hexdigest(), 'output_sha256': hashlib.sha256(built).hexdigest(),
                'output': str(candidate), 'changed_classes': changed, 'unchanged_classes': len(originals) - len(changed),
                'non_script_tags_preserved': True, 'unrelated_methods_preserved': True, 'handler_tests_passed': True}
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    print(json.dumps(manifest, indent=2))


if __name__ == '__main__':
    main()
