"""Build the August team queue fallback from the exact installed UIRoot asset.

The positive-team branch otherwise opens PostTransferCharacterWindow without sending
EC when no Steam lobby exists. Existing Steam owner/follower and invitational paths
are preserved. Builds only; never modifies an installed client.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess

SOURCE_SHA256 = 'dac8eb53d1aeb2b50c63e8984f80260f126c99562e1fc2d0a4685623d9696bd4'
CLASS = 'views.mainmenu.UiMainMenuManager'
RELATIVE = Path('scripts/views/mainmenu/UiMainMenuManager.as')
METHOD = 'protected function requestEnterGameQueue'


def method_span(source):
    header = source.index(METHOD)
    start = source.index('{', header)
    depth, at = 1, start + 1
    while depth:
        if source[at] == '{':
            depth += 1
        elif source[at] == '}':
            depth -= 1
        at += 1
    return header, at


def patch_source(source):
    original = '''                  if(_loc2_ && _loc3_ || Boolean(UIBindingSystem.InInvitational()))
                  {
                     this.CallGroupTransfer();
                  }'''
    replacement = original + '''
                  else if(!_loc2_)
                  {
                     SharedGlobalData.GetInstance().isTeam2 = this.m_lobbyType == 2;
                     UIBindingCharacterCreate.TransferCharacter(this.m_playerGuid,this.m_currentlySelectedServer);
                     SharedGlobalData.GetInstance().isInGroupGame = true;
                     this.m_isObserver = false;
                  }'''
    if source.count(original) != 1:
        raise ValueError('Expected the exact unmodified August team queue gate')
    return source.replace(original, replacement, 1)


def normalized_source(source):
    # FFDec elides these redundant conversions/parentheses while compiling this
    # class. param1 is declared String and LoginQueueRow.queuePosition returns int.
    # Accept only these observed equivalent forms, not general source rewrites.
    return source.replace('String(param1).split(";")', 'param1.split(";")').replace(
        'if((Boolean(_loc7_)) &&', 'if(Boolean(_loc7_) &&').replace(
        'if((Boolean(_loc8_)) &&', 'if(Boolean(_loc8_) &&').replace(
        'this.processLoginQueueDetails(int(this.m_loginQueue.queuePosition),_loc1_);',
        'this.processLoginQueueDetails(this.m_loginQueue.queuePosition,_loc1_);')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('asset', type=Path)
    parser.add_argument('--ffdec', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    asset, out = args.asset.resolve(), args.out.resolve()
    raw = asset.read_bytes()
    if hashlib.sha256(raw).hexdigest() != SOURCE_SHA256:
        raise ValueError('Installed UIRoot changed; rebase and verify instead of replacing later edits')
    out.mkdir(parents=True, exist_ok=True)
    java = ['java', '-jar', str(args.ffdec.resolve())]
    subprocess.run(java + ['-selectclass', CLASS, '-export', 'script', str(out / 'source'), str(asset)], check=True)
    script = out / 'source' / RELATIVE
    original = script.read_text()
    script.write_text(patch_source(original))
    compiled = out / 'compiled.gfx'
    subprocess.run(java + ['-onerror', 'abort', '-importScript', str(asset), str(compiled), str(out / 'source/scripts')], check=True)
    spec = importlib.util.spec_from_file_location('tag_preserver', Path(__file__).with_name('build-helmet-click.py'))
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)
    built = helper.preserve_tags(raw, compiled.read_bytes())
    destination = out / 'UIRoot.gfx'
    destination.write_bytes(built)
    subprocess.run(java + ['-selectclass', CLASS, '-export', 'script', str(out / 'verify'), str(destination)], check=True)
    verified = (out / 'verify' / RELATIVE).read_text()
    start, end = method_span(original)
    verified_start, verified_end = method_span(verified)
    if normalized_source(original[:start] + original[end:]) != normalized_source(verified[:verified_start] + verified[verified_end:]):
        raise ValueError('An unrelated part of the menu class changed during compilation')
    method = verified[verified_start:verified_end]
    for required in ('else if(!_loc2_)', 'SharedGlobalData.GetInstance().isTeam2 = this.m_lobbyType == 2;',
                     'UIBindingCharacterCreate.TransferCharacter(this.m_playerGuid,this.m_currentlySelectedServer);',
                     'SharedGlobalData.GetInstance().isInGroupGame = true;'):
        if required not in method:
            raise ValueError('Re-exported fallback lacks ' + required)
    subprocess.run(['node', str(Path(__file__).with_name('verify-local-team-queue.cjs')), str(out / 'verify' / RELATIVE)], check=True)
    # Check the entire imported movie too, including earlier work in other classes.
    # A one-DoABC check alone is insufficient because UIRoot's ABC contains all 275.
    subprocess.run(java + ['-export', 'script', str(out / 'all-original'), str(asset)], check=True)
    subprocess.run(java + ['-export', 'script', str(out / 'all-patched'), str(destination)], check=True)
    before, after = out / 'all-original/scripts', out / 'all-patched/scripts'
    originals = {path.relative_to(before): path for path in before.rglob('*.as')}
    patched = {path.relative_to(after): path for path in after.rglob('*.as')}
    if originals.keys() != patched.keys():
        raise ValueError('Compiled movie changed its class set')
    changed_classes = [path for path in originals if originals[path].read_bytes() != patched[path].read_bytes()]
    if changed_classes != [Path('views/mainmenu/UiMainMenuManager.as')]:
        raise ValueError('An unrelated class changed: ' + str(changed_classes))
    manifest = {
        'asset': 'UIRoot.gfx', 'pack': 'Assets_060.pack', 'source_sha256': SOURCE_SHA256,
        'output_sha256': hashlib.sha256(built).hexdigest(), 'output': str(destination),
        'changed_class': CLASS, 'changed_method': METHOD,
        'one_changed_doabc': True, 'other_tags_and_trailer_preserved': True,
        'other_menu_class_source_equivalent': True,
        'other_exported_scripts_unchanged': len(originals) - 1,
        'compiler_normalizations': ['redundant String cast on String parameter',
                                    'two redundant Boolean-expression parentheses',
                                    'redundant int cast on int getter'],
    }
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print(json.dumps(manifest, indent=2))


if __name__ == '__main__':
    main()
