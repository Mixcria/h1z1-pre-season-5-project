"""Build direct main-menu startup on the cumulative September 8 August UIRoot.

First-time Appearance is a client preference, independent of the server LoginZone.
Preserve every other class and GFx tag; ordinary Appearance navigation remains.
See docs/starter-accounts-20260909.md. Builds a reviewable asset without installing.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess

SOURCE_SHA256 = '818b3795988979bfaad3987f4436166d5f77f3d85efe626fd5726a643a751b53'
RELATIVE = Path('views/mainmenu/UiMainMenuManager.as')
OLD = 'SharedGlobalData.GetInstance().isNPX = UIBindingSettings.GetFirstTimeEventEnabled() == true;'
NEW = 'SharedGlobalData.GetInstance().isNPX = false;'


def normalized(source):
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
    original = asset.read_bytes()
    if hashlib.sha256(original).hexdigest() != SOURCE_SHA256:
        raise ValueError('Installed UIRoot changed; rebase this patch before building')
    out.mkdir(parents=True, exist_ok=False)
    java = ['java', '-jar', str(args.ffdec.resolve())]
    subprocess.run(java + ['-export', 'script', str(out / 'source'), str(asset)], check=True)
    source_root = out / 'source/scripts'
    script = (source_root / RELATIVE).read_text(encoding='utf-8')
    if script.count(OLD) != 1:
        raise ValueError('Unexpected first-time startup implementation')
    expected = script.replace(OLD, NEW)
    patch = out / 'patch' / RELATIVE
    patch.parent.mkdir(parents=True)
    patch.write_text(expected, encoding='utf-8')
    compiled = out / 'compiled.gfx'
    subprocess.run(java + ['-onerror', 'abort', '-importScript', str(asset), str(compiled),
                          str(out / 'patch')], check=True)
    spec = importlib.util.spec_from_file_location('menu_start_tags', Path(__file__).with_name('build-helmet-click.py'))
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)
    built = helper.preserve_tags(original, compiled.read_bytes())
    destination = out / 'UIRoot.gfx'
    destination.write_bytes(built)
    subprocess.run(java + ['-export', 'script', str(out / 'verified'), str(destination)], check=True)
    verified_root = out / 'verified/scripts'
    sources = {p.relative_to(source_root): p.read_text(encoding='utf-8') for p in source_root.rglob('*.as')}
    verified = {p.relative_to(verified_root): p.read_text(encoding='utf-8') for p in verified_root.rglob('*.as')}
    if sources.keys() != verified.keys():
        raise ValueError('Class set changed')
    changed = [name for name in sources if sources[name] != verified[name]]
    if changed != [RELATIVE] or normalized(expected) != normalized(verified[RELATIVE]):
        raise ValueError('Re-exported movie differs from the single intended startup assignment')
    for retained in ('this.setMenuById(MenuItemId.ROOT,0,"kotkdefault");',
                     'MainMenuEvent.SHOW_CUSTOMIZATION,this.showCustomization',
                     'MainMenuEvent.SHOW_MARKETPLACE_CRATES,this.showMarketplaceCrates'):
        if retained not in verified[RELATIVE]:
            raise ValueError('Ordinary navigation missing: ' + retained)
    result = {'asset': 'UIRoot.gfx', 'pack': 'Assets_060.pack', 'source_sha256': SOURCE_SHA256,
              'output_sha256': hashlib.sha256(built).hexdigest(), 'output': str(destination),
              'changed_classes': ['views.mainmenu.UiMainMenuManager'],
              'other_exported_scripts_unchanged': len(sources) - 1,
              'one_changed_doabc': True, 'other_tags_and_trailer_preserved': True,
              'first_time_appearance_skipped': True, 'ordinary_appearance_navigation_preserved': True,
              'other_main_menu_code_unchanged': True}
    (out / 'manifest.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
