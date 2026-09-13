"""Enable native hold-to-sprint/Shift prone-roll in the canonical August client.

Default is dry-run. Apply requires H1Z1 closed and saves an exact UserOptions.ini
backup. Restore refuses settings changed since installation. No executable changes.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import tempfile

OPTIONS = Path('C:/Aug2017/Client/UserOptions.ini')
spec = importlib.util.spec_from_file_location('pack_safety', Path(__file__).with_name('install-bounty-lobby.py'))
safety = importlib.util.module_from_spec(spec)
spec.loader.exec_module(safety)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def sprint_hold(data):
    """Change exactly General.SprintToggle, retaining every unrelated byte."""
    headers = list(re.finditer(rb'(?mi)^\[([^\r\n]+)\][ \t]*\r?$', data))
    selected = [i for i, header in enumerate(headers) if header[1].lower() == b'general']
    if len(selected) != 1:
        raise ValueError('Expected exactly one General section')
    index = selected[0]
    start = headers[index].end()
    end = headers[index + 1].start() if index + 1 < len(headers) else len(data)
    section = data[start:end]
    matches = list(re.finditer(rb'(?mi)^(SprintToggle[ \t]*=[ \t]*)([^\r\n]*)', section))
    if len(matches) > 1:
        raise ValueError('Duplicate General.SprintToggle keys')
    if matches:
        match = matches[0]
        previous = match[2].decode('ascii')
        if previous.strip() not in ('0', '1'):
            raise ValueError('Unexpected SprintToggle value')
        section = section[:match.start(2)] + b'0' + section[match.end(2):]
    else:
        previous = None
        newline = b'\r\n' if b'\r\n' in data else b'\n'
        # Header regex consumes CR but leaves LF; prepend after that LF.
        at = 1 if section.startswith(b'\n') else 0
        section = section[:at] + b'SprintToggle=0' + newline + section[at:]
    return data[:start] + section + data[end:], previous


def replace_checked(path, raw, expected):
    safety.require_client_closed()
    fd, temporary = tempfile.mkstemp(prefix='UserOptions.sprint-', suffix='.tmp', dir=path.parent)
    staged = Path(temporary)
    try:
        with os.fdopen(fd, 'wb') as output:
            output.write(raw)
            output.flush()
            os.fsync(output.fileno())
        safety.require_client_closed()
        if digest(path.read_bytes()) != expected:
            raise ValueError('Client settings changed during preparation')
        os.replace(staged, path)
        if path.read_bytes() != raw:
            raise ValueError('Client settings readback differs')
    finally:
        staged.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--backup', type=Path)
    parser.add_argument('--restore', type=Path)
    args = parser.parse_args()
    current = OPTIONS.read_bytes()
    if args.restore:
        manifest = json.loads(args.restore.read_text())
        raw = (args.restore.parent / 'UserOptions.ini').read_bytes()
        if digest(raw) != manifest['original_sha256'] or digest(current) != manifest['installed_sha256']:
            raise ValueError('Backup or installed settings changed; refusing restore')
        if args.apply:
            replace_checked(OPTIONS, raw, digest(current))
        print('Sprint settings restored' if args.apply else 'Sprint restore dry run')
        return
    changed, previous = sprint_hold(current)
    manifest = {'options': str(OPTIONS), 'previous_sprint_toggle': previous,
                'sprint_toggle': 0, 'original_sha256': digest(current),
                'installed_sha256': digest(changed)}
    if args.apply and changed != current:
        if args.backup is None:
            parser.error('--apply requires --backup')
        safety.require_client_closed()
        args.backup.mkdir(parents=True, exist_ok=False)
        (args.backup / 'UserOptions.ini').write_bytes(current)
        (args.backup / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
        replace_checked(OPTIONS, changed, digest(current))
    print(json.dumps({'action': 'applied' if args.apply else 'dry run', **manifest}, indent=2))


if __name__ == '__main__':
    main()
