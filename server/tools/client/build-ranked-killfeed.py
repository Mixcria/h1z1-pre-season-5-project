"""Keep August's retail kill feed; add red staff badges and restore its omitted division reads."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import struct
import zlib


def non_script_tags(path):
    raw = path.read_bytes()
    body = zlib.decompress(raw[8:]) if raw[:3] in (b'CFX', b'CWS') else raw[8:]
    position = (5 + (body[0] >> 3) * 4 + 7) // 8 + 4
    result = []
    while position < len(body):
        header, = struct.unpack_from('<H', body, position)
        position += 2
        code, size = header >> 6, header & 63
        if size == 63:
            size, = struct.unpack_from('<I', body, position)
            position += 4
        payload = body[position:position + size]
        assert len(payload) == size
        if code != 82:
            result.append((code, payload))
        position += size
        if code == 0:
            break
    return result

def patch(scripts):
    changed = []
    for name, players in [('KillNotification', ['killer', 'killed']),
                          ('DeathNotification', ['killed']),
                          ('AssistNotification', ['killer', 'killed', 'assist'])]:
        p = scripts / 'views/hudkillfeed' / (name + '.as')
        text = p.read_text()
        text = text.replace('   import ui.constants.Colors;', '   import ui.constants.Colors;\n   import flash.geom.ColorTransform;')
        for who in players:
            field = 'm_notifParams.' + who + 'Tier'
            assert text.count(field + ' < 8') == 1
            text = text.replace(field + ' < 8', field + ' <= 8')
            marker = '                  if(' + field + ' == 7'
            assert marker in text
            text = text.replace(marker, '''                  this.m_''' + who + '''Tier.visible = true;
                  this.m_''' + who + '''Tier.m_icon.visible = true;
                  this.m_''' + who + '''Tier.m_bg.transform.colorTransform = new ColorTransform();
                  if(''' + field + ''' == 8)
                  {
                     this.m_''' + who + '''Tier.m_bg.gotoAndStop(1);
                     this.m_''' + who + '''Tier.m_icon.visible = false;
                     var staffColor:ColorTransform = new ColorTransform();
                     staffColor.color = 0xD92332;
                     this.m_''' + who + '''Tier.m_bg.transform.colorTransform = staffColor;
                  }
                  else if(''' + field + ' == 7', 1)
            # The retail clip has no staff frame. Hide the rank emblem and remove
            # its width from both the nameplate and the following text position.
            icon_width = 'this.m_' + who + 'Tier.m_icon.width'
            assert text.count(icon_width) == 2
            text = text.replace(icon_width, '(' + field + ' == 8 ? 0 : ' + icon_width + ')')
            # Staff has no emblem: remove the emblem's remaining nine-pixel text
            # inset too, and contract the plate by that same amount. Ordinary
            # rank layout keeps both its original artwork width and padding.
            advance = '(' + field + ' == 8 ? 0 : ' + icon_width + ') + SPACING * 3'
            assert text.count(advance) == 1
            text = text.replace(advance, '(' + field + ' == 8 ? 0 : ' + icon_width + ' + SPACING * 3)')
            plate = 'this.m_' + who + 'Name.width + TIER_BG_PADDING;'
            assert text.count(plate) == 1
            text = text.replace(plate, 'this.m_' + who + 'Name.width + (' + field
                + ' == 8 ? TIER_BG_PADDING - SPACING * 3 : TIER_BG_PADDING);')
        # Tint only the nameplate. ColorTransform.color replaces every RGB value;
        # applying it to the icon flattens the badge artwork into a solid circle.
        # AS3 variables are function-scoped; use unique locals for each player's colour.
        for who in players:
            start = text.index('if(m_notifParams.' + who + 'Tier == 8)')
            end = text.index('else if', start)
            text = text[:start] + text[start:end].replace('staffColor', who + 'StaffColor') + text[end:]
        p.write_text(text)
        changed.append(p)
    p = scripts / 'views/hudkillfeed/HudKillFeedWindow.as'
    text = p.read_text()
    text = text.replace('_loc5_.killedTier = int(_loc2_[10]);',
        '_loc5_.killedTier = int(_loc2_[10]);\n            _loc5_.killedSubtier = int(_loc2_[int(_loc2_[1]) == 0 ? 11 : (int(_loc2_[1]) == 1 ? 16 : 22)]);')
    text = text.replace('_loc5_.killerTier = int(_loc2_[15]);',
        '_loc5_.killerTier = int(_loc2_[15]);\n                  _loc5_.killerSubtier = int(_loc2_[int(_loc2_[1]) == 1 ? 17 : 23]);')
    text = text.replace('_loc5_.assistTier = int(_loc2_[20]);',
        '_loc5_.assistTier = int(_loc2_[20]);\n                  _loc5_.assistSubtier = int(_loc2_[24]);')
    p.write_text(text)
    changed.append(p)
    return changed

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('source', type=Path)
    parser.add_argument('--ffdec', required=True, type=Path)
    parser.add_argument('--out', required=True, type=Path)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    java = ['java', '-jar', str(args.ffdec)]
    subprocess.run(java + ['-export', 'script', str(args.out / 'source'), str(args.source)], check=True)
    changed = patch(args.out / 'source/scripts')
    # Import only the four changed classes, preserving every other retail script.
    import shutil
    for p in changed:
        dest = args.out / 'patch' / p.relative_to(args.out / 'source/scripts')
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(p, dest)
    output = args.out / 'HudKillFeedWindow.gfx'
    subprocess.run(java + ['-onerror', 'abort', '-importScript', str(args.source), str(output), str(args.out / 'patch')], check=True)
    subprocess.run(java + ['-export', 'script', str(args.out / 'verify'), str(output)], check=True)
    assert non_script_tags(args.source) == non_script_tags(output), 'Non-script SWF tags changed'
    changed_paths = {p.relative_to(args.out / 'source/scripts') for p in changed}
    for original in (args.out / 'source/scripts').rglob('*.as'):
        relative = original.relative_to(args.out / 'source/scripts')
        if relative not in changed_paths:
            assert original.read_text() == (args.out / 'verify/scripts' / relative).read_text(), relative
    for p in changed:
        checked = args.out / 'verify/scripts' / p.relative_to(args.out / 'source/scripts')
        assert checked.exists()
        if p.name != 'HudKillFeedWindow.as':
            checked_text = checked.read_text()
            assert 'ColorTransform' in checked_text and '14230322' in checked_text
            assert 'm_icon.transform.colorTransform' not in checked_text
            player_count = {
                'KillNotification.as': 2, 'DeathNotification.as': 1,
                'AssistNotification.as': 3}[p.name]
            assert checked_text.count('m_bg.transform.colorTransform') == player_count * 2
            assert checked_text.count('m_icon.visible = false') == player_count
            assert checked_text.count('m_icon.visible = true') == player_count
            assert checked_text.count('Tier == 8 ? 0 :') == player_count * 2
            assert 'm_icon.gotoAndStop(1)' not in checked_text
    subprocess.run(['node', str(Path(__file__).with_name('verify-ranked-killfeed.cjs')),
                    str(args.out / 'verify/scripts')], check=True)
    (args.out / 'manifest.json').write_text(json.dumps({
        'source': str(args.source), 'sourceSha256': hashlib.sha256(args.source.read_bytes()).hexdigest(),
        'outputSha256': hashlib.sha256(output.read_bytes()).hexdigest(),
        'changes': ['staff tier 8 red nameplate without a competitive rank emblem or its allocated width',
                    'staff name starts at the emblem origin, with no residual nine-pixel inset',
                    'native division fields restored']}, indent=2))
