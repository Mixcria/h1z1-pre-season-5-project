"""Build native command prefix normalization on the cumulative installed August UIRoot.

Preserves the hosted menu/help, team queue, inventory movement and loot-bag refresh. Uses
the existing GFx tag-preservation workflow and re-exports every script to prove
that only the native console submit argument changes. Never installs the asset.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess

SOURCE_SHA256 = '3ea539539dd032687eb1ab14f28b3303bcde1f1227f20d2138aec066c7f3aed4'
CLASS = 'views.console.UIConsoleManager'
RELATIVE = Path('views/console/UIConsoleManager.as')


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def normalized(source):
    # Existing verified help build already contains this compiler normalization.
    source = source.replace('_loc10_ = _loc10_ + ', '_loc10_ += ')
    lines = [line.strip() for line in source.splitlines() if line.strip()]
    return '\n'.join(sorted(line for line in lines if line.startswith('import '))
                     + [line for line in lines if not line.startswith('import ')])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('asset', type=Path)
    parser.add_argument('--ffdec', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    asset, out = args.asset.resolve(), args.out.resolve()
    raw = asset.read_bytes()
    if hashlib.sha256(raw).hexdigest() != SOURCE_SHA256:
        raise ValueError('UIRoot changed; rebase and verify instead of replacing later edits')
    if out == asset.parent:
        raise ValueError('Build output must be separate from the source asset')
    out.mkdir(parents=True, exist_ok=False)
    java = ['java', '-jar', str(args.ffdec.resolve())]
    subprocess.run(java + ['-export', 'script', str(out / 'source'), str(asset)], check=True)
    source_root = out / 'source/scripts'
    original = (source_root / RELATIVE).read_text(encoding='utf-8')
    alias = load('console_command_alias', 'console-help-alias.py')
    expected = alias.patch_source(original)
    patch = out / 'patch' / RELATIVE
    patch.parent.mkdir(parents=True)
    patch.write_text(expected, encoding='utf-8')
    compiled = out / 'compiled.gfx'
    subprocess.run(java + ['-onerror', 'abort', '-importScript', str(asset), str(compiled),
                          str(out / 'patch')], check=True)
    helper = load('console_tag_preserver', 'build-helmet-click.py')
    built = helper.preserve_tags(raw, compiled.read_bytes())
    destination = out / 'UIRoot.gfx'
    destination.write_bytes(built)
    subprocess.run(java + ['-export', 'script', str(out / 'verified'), str(destination)], check=True)
    verified_root = out / 'verified/scripts'
    originals = {path.relative_to(source_root): path for path in source_root.rglob('*.as')}
    verified = {path.relative_to(verified_root): path for path in verified_root.rglob('*.as')}
    if originals.keys() != verified.keys():
        raise ValueError('Compiled movie changed its class set')
    changed = [path for path in originals if originals[path].read_bytes() != verified[path].read_bytes()]
    if changed != [RELATIVE]:
        raise ValueError('An unrelated class changed: ' + str(changed))
    if normalized(expected) != normalized(verified[RELATIVE].read_text(encoding='utf-8')):
        raise ValueError('Re-exported console differs from the exact intended alias patch')
    subprocess.run(['node', str(Path(__file__).with_name('verify-console-help.cjs')),
                    str(verified[RELATIVE])], check=True)
    subprocess.run(['node', str(Path(__file__).with_name('verify-hosted-games.cjs')),
                    str(verified_root / 'views/events/UIEventsManager.as')], check=True)
    subprocess.run(['node', str(Path(__file__).with_name('verify-inventory-refresh.cjs')),
                    str(verified_root / 'views/inventory/UIInventoryManager.as')], check=True)
    subprocess.run(['node', str(Path(__file__).with_name('verify-inventory-movement.cjs')),
                    str(verified_root / 'ui/states/UIState_InGame.as'), str(verified_root)], check=True)
    manifest = {
        'asset': 'UIRoot.gfx', 'pack': 'Assets_060.pack',
        'source': str(asset), 'source_sha256': SOURCE_SHA256,
        'output': str(destination), 'output_sha256': hashlib.sha256(built).hexdigest(),
        'changed_classes': [CLASS], 'other_exported_scripts_unchanged': len(originals) - 1,
        'one_changed_doabc': True, 'other_tags_and_trailer_preserved': True,
        'exact_console_patch_verified': True,
        'prefixes': 'bare command and ./command normalize to /command; original names and handlers retained',
        'help_alias_preserved': '/help maps to /commands',
        'original_local_input_and_arguments_preserved': True,
        'source_patches_preserved': ['hosted games and help', 'team queue', 'inventory movement', 'loot-bag refresh'],
    }
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(manifest, indent=2))


if __name__ == '__main__':
    main()
