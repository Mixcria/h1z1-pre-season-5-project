#!/usr/bin/env python3
"""Add thirteen locked crate wrappers and a Legacy Crate label; dry run by default.

Preserves existing item definitions, every other pack asset, and existing locale
records. Requires the game closed for installation. Writes verified backups and
uses atomic replacements, separating any existing hardlinks.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'data'))
from locked_special_crates import extend_client_sheet, LEGACY_NAME_ID


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec)
    sys.modules[name] = result
    spec.loader.exec_module(result)
    return result


pack_tools = module('crate_pack_tools', Path(__file__).with_name('install-crate-unlock-ten.py'))
locale_tools = module('crate_locale_tools', Path(__file__).resolve().parents[1] / 'locale/localedat.py')
pack_tools.NAME = 'ClientItemDefinitions.txt'
pack_tools.PACK = 'Assets_055.pack'
STOCK_SHEET = '638deb01b4eeb565d1c8fc2037e0169518a2c54dd23f19c776f25d3e39dab114'


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def update_locale_checksum(dat, directory):
    """The native loader checks the entire .dat, including its UTF-8 BOM."""
    checksum = hashlib.md5(dat).hexdigest().upper().encode('ascii')
    result, count = re.subn(rb'(## MD5Checksum:[ \t]*)[0-9A-Fa-f]{32}(?=\r\n)',
        lambda match: match[1] + checksum, directory)
    if count != 1:
        raise ValueError('Expected exactly one locale MD5Checksum header')
    return result


def extend_locale(dat, directory):
    key = locale_tools.text_key(LEGACY_NAME_ID)
    lines = directory.decode('utf-8-sig').split('\r\n')
    if any(line.startswith(str(key) + '\t') for line in lines):
        raise ValueError('Custom Legacy label key already exists; inspect the installation')
    if not dat.endswith(b'\r\n'):
        raise ValueError('Expected newline-terminated locale data')
    record = f'{key}\tugdt\tLegacy Crate'.encode('utf-8')
    headers = [line for line in lines if line.startswith('##')]
    rows = [line for line in lines if line and not line.startswith('##')]
    if len([h for h in headers if h.startswith('## Count:')]) != 1:
        raise ValueError('Missing locale record count')
    headers = [re.sub(r'(?<=Count:)\s*\d+', '\t' + str(len(rows) + 1), h) for h in headers]
    records = {}
    cursor = 3 if dat.startswith(b'\xef\xbb\xbf') else 0
    for row in rows:
        old_key, offset, length, flag = row.split('\t')
        old_key, offset, length = int(old_key), int(offset), int(length)
        body = dat[offset:offset + length]
        if (old_key in records or offset != cursor or len(body) != length
                or not body.startswith(str(old_key).encode() + b'\t')
                or dat[offset + length:offset + length + 2] != b'\r\n'):
            raise ValueError('Expected complete locale records in physical order')
        records[old_key] = (body, flag)
        cursor = offset + length + 2
    if cursor != len(dat):
        raise ValueError('Locale data contains unindexed bytes')
    records[key] = (record, 'd')
    # Native FUN_1417c94b0 rejects non-increasing .dat offsets (0x3404).
    # Insert the new record into both files; preserve each existing record verbatim.
    patched_dat = bytearray(dat[:3] if dat.startswith(b'\xef\xbb\xbf') else b'')
    rows = []
    for record_key, (body, flag) in sorted(records.items()):
        rows.append(f'{record_key}\t{len(patched_dat)}\t{len(body)}\t{flag}')
        patched_dat.extend(body + b'\r\n')
    bom = b'\xef\xbb\xbf' if directory.startswith(b'\xef\xbb\xbf') else b''
    patched_dat = bytes(patched_dat)
    patched_dir = bom + ('\r\n'.join(headers + rows) + '\r\n').encode('utf-8')
    return patched_dat, update_locale_checksum(patched_dat, patched_dir)


def atomic_replace(path, raw):
    fd, temporary = tempfile.mkstemp(prefix=path.name + '.locked-crates-', dir=path.parent)
    try:
        with os.fdopen(fd, 'wb') as target:
            target.write(raw)
            target.flush()
            os.fsync(target.fileno())
        if digest(Path(temporary).read_bytes()) != digest(raw):
            raise ValueError('Staged file verification failed')
        os.replace(temporary, path)
    finally:
        if Path(temporary).exists():
            Path(temporary).unlink()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--client', type=Path, default=Path('C:/Aug2017/Client'))
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    client, out = args.client.resolve(), args.out.resolve()
    pack = client / 'Resources/Assets/Assets_055.pack'
    dat_path, dir_path = (client / 'Locale' / ('en_us_data.' + suffix) for suffix in ('dat', 'dir'))
    before = {path: path.read_bytes() for path in (pack, dat_path, dir_path)}
    entry = next(e for e in pack_tools.entries(before[pack]) if e[0] == pack_tools.NAME)
    sheet = before[pack][entry[2]:entry[2] + entry[3]]
    if digest(sheet) != STOCK_SHEET:
        raise ValueError('Native item definitions changed; refusing to replace another modification')
    patched_sheet = extend_client_sheet(sheet)
    patched_pack, _, other_assets = pack_tools.prepare_update(before[pack], patched_sheet,
        old_hash=STOCK_SHEET, new_hash=digest(patched_sheet))
    patched_dat, patched_dir = extend_locale(before[dat_path], before[dir_path])
    replacement = {pack: patched_pack, dat_path: patched_dat, dir_path: patched_dir}
    out.mkdir(parents=True, exist_ok=True)
    (out / 'ClientItemDefinitions.txt').write_bytes(patched_sheet)
    locale_out = out / 'locale'
    locale_out.mkdir(exist_ok=True)
    (locale_out / dat_path.name).write_bytes(patched_dat)
    (locale_out / dir_path.name).write_bytes(patched_dir)
    old_locale = locale_tools.LocaleData(locale_dir=client / 'Locale')
    new_locale = locale_tools.LocaleData(locale_dir=locale_out)
    if any(new_locale.records.get(key) != record for key, record in old_locale.records.items()):
        raise ValueError('An existing locale record changed')
    if new_locale.text(LEGACY_NAME_ID) != 'Legacy Crate':
        raise ValueError('New locked Legacy label did not resolve')
    manifest = {'client': str(client), 'added_locked_definitions': 13,
        'other_assets_unchanged': other_assets, 'existing_locale_records_preserved': len(old_locale.records),
        'files': [{'path': str(path), 'before_sha256': digest(before[path]),
                   'after_sha256': digest(raw)} for path, raw in replacement.items()]}
    if args.apply:
        pack_tools.require_client_closed()
        backup = out / 'backup'
        backup.mkdir(exist_ok=False)
        for index, row in enumerate(manifest['files']):
            path = Path(row['path'])
            saved = backup / path.name
            saved.write_bytes(before[path])
            if digest(saved.read_bytes()) != row['before_sha256']:
                raise ValueError('Backup verification failed')
            row['backup'] = str(saved)
        (out / 'install-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
        pack_tools.require_client_closed()
        if any(digest(path.read_bytes()) != digest(raw) for path, raw in before.items()):
            raise ValueError('Client files changed during preparation')
        installed = []
        try:
            # Publish the new label before any item definition refers to it.
            for path in (dat_path, dir_path, pack):
                atomic_replace(path, replacement[path])
                installed.append(path)
                if digest(path.read_bytes()) != digest(replacement[path]):
                    raise ValueError('Installed file verification failed')
        except Exception:
            for path in reversed(installed):
                if digest(path.read_bytes()) == digest(replacement[path]):
                    atomic_replace(path, before[path])
            raise
    print(json.dumps({'action': 'installed' if args.apply else 'verified dry run', **manifest}, indent=2))


if __name__ == '__main__':
    main()
