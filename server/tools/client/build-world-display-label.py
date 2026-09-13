"""Replace the August compass version label with the admitted world's server-supplied name."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

CLASS = Path('views/hudcompass/HudCompassWindow.as')
KEY = 'Cranberry.WorldDisplayName'


def patch(source):
    """Change only the compass class; reject missing or previously modified source anchors."""
    imports = '   import flash.text.TextField;'
    before = '''            this.m_clientVersion.visible = Boolean(UIBindingSettings.GetClientVersionDisplayEnabled()) && !Boolean(UIBindingSystem.InInvitational());
            this.m_clientVersion.text = "v" + String(UIBindingSystem.GetClientVersion());'''
    after = '''            var worldName:String = String(UIBindingSystem.GetStringHashValue("Cranberry.WorldDisplayName",""));
            this.m_clientVersion.autoSize = TextFieldAutoSize.RIGHT;
            this.m_clientVersion.visible = worldName.length > 0 || Boolean(UIBindingSettings.GetClientVersionDisplayEnabled()) && !Boolean(UIBindingSystem.InInvitational());
            this.m_clientVersion.text = worldName.length > 0 ? worldName : "v" + String(UIBindingSystem.GetClientVersion());
            if(this.m_serverName != null)
            {
               this.m_serverName.visible = worldName.length == 0 && !Boolean(UIBindingSystem.InInvitational());
            }'''
    enter_before = '            this.m_serverName.text = String(UIBindingSystem.GetWorldDisplayName());\n         }'
    enter_after = enter_before + '\n         this.updateClientVersionVisibility();'
    tick_before = '         var _loc1_:BattleRoyaleDataObject = null;'
    tick_after = tick_before + '\n         this.updateClientVersionVisibility();'
    for old, new in [(imports, imports + '\n   import flash.text.TextFieldAutoSize;'),
                     (before, after), (enter_before, enter_after), (tick_before, tick_after)]:
        if source.count(old) != 1:
            raise ValueError('Compass source is unexpected or already patched: ' + old[:75])
        source = source.replace(old, new, 1)
    return source


def verify(source):
    required = [KEY, 'TextFieldAutoSize.RIGHT',
                'worldName.length > 0 ? worldName : "v" + String(UIBindingSystem.GetClientVersion())',
                'worldName.length == 0 && !Boolean(UIBindingSystem.InInvitational())',
                'this.m_clientVersion.visible = false;', 'this.m_serverName.visible = false;']
    for value in required:
        if value not in source:
            raise ValueError('Exported compass is missing ' + value)
    if source.count('this.updateClientVersionVisibility();') != 3:
        raise ValueError('Compass must refresh its label on entry and on the existing position tick')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('--ffdec', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    java = ['java', '-jar', str(args.ffdec)]
    subprocess.run(java + ['-export', 'script', str(args.out / 'source'), str(args.source)], check=True)
    script = args.out / 'source/scripts' / CLASS
    changed = patch(script.read_text())
    target = args.out / 'patch' / CLASS
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(changed)
    output = args.out / 'HudCompassWindow.gfx'
    subprocess.run(java + ['-onerror', 'abort', '-importScript', str(args.source), str(output), str(args.out / 'patch')], check=True)
    subprocess.run(java + ['-export', 'script', str(args.out / 'verify'), str(output)], check=True)
    verify((args.out / 'verify/scripts' / CLASS).read_text())
    source_root, verified_root = args.out / 'source/scripts', args.out / 'verify/scripts'
    for path in source_root.rglob('*.as'):
        relative = path.relative_to(source_root)
        if relative != CLASS and path.read_bytes() != (verified_root / relative).read_bytes():
            raise ValueError('An unrelated script changed: ' + str(relative))
    (args.out / 'manifest.json').write_text(json.dumps({
        'source': str(args.source), 'sourceSha256': hashlib.sha256(args.source.read_bytes()).hexdigest(),
        'output': str(output), 'outputSha256': hashlib.sha256(output.read_bytes()).hexdigest(),
        'key': KEY, 'class': str(CLASS),
        'changes': ['server-supplied admitted world name replaces the version label',
                    'existing one-second compass refresh picks up world changes',
                    'right-aligned autosize preserves the label edge for longer world names',
                    'original version fallback, settings and spectator visibility remain available']}, indent=2))


if __name__ == '__main__':
    main()
