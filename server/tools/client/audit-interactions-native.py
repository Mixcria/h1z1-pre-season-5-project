#!/usr/bin/env python3
"""Read-only August interaction/colour evidence. Requires pefile and capstone; never opens a process."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path

import capstone
import pefile

HERE = Path(__file__).resolve().parent


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    original = args.exe.read_bytes()
    digest = hashlib.sha256(original).hexdigest()
    if digest != 'd949d39f45074f2b223257477803a8858b4970242c6963df9a213a169d8929dd':
        raise ValueError('Expected the reviewed August 0.0.118.208059 executable')
    if args.out.resolve().is_relative_to(args.exe.resolve().parent):
        raise ValueError('Evidence must be outside the client directory')
    pe = pefile.PE(data=original, fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase

    def read(va, count):
        at = pe.get_offset_from_rva(va - base)
        return original[at:at + count]

    # Import only the existing classifiers; do not construct LiveProcess or call a patch writer.
    spec = importlib.util.spec_from_file_location('interactions_live_evidence', HERE / 'loot-during-reload-live.py')
    live = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(live)
    functions = {}
    for name, rva, expected, patched in (
        ('interaction', live.FUNCTION_RVA, live.evidence.ORIGINAL_FUNCTION, live.PATCHED_FUNCTION_V2),
        ('throttle', live.throttle.FUNCTION_RVA, live.throttle.ORIGINAL_FUNCTION, live.throttle.PATCHED_FUNCTION),
    ):
        actual = read(base + rva, len(expected))
        if actual != expected:
            raise ValueError(f'{name}: full function differs from the existing reviewed helper')
        functions[name] = {'va': hex(base + rva), 'length': len(actual),
                           'original_sha256': hashlib.sha256(actual).hexdigest(),
                           'existing_patched_sha256': hashlib.sha256(patched).hexdigest()}
    state = live.classify_state(read(base + live.FUNCTION_RVA, 468),
                               read(base + live.throttle.FUNCTION_RVA, 283))
    args.out.mkdir(parents=True, exist_ok=True)
    disassembler = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    regions = {}
    for name, start, end in (
        ('damage-parser', 0x140a34ea0, 0x140a34fc0),
        ('damage-callsite', 0x140afcbb4, 0x140afcc3d),
        ('damage-consumer', 0x1411ada40, 0x1411adf00),
        ('damage-renderer', 0x141399a00, 0x141399bd5),
        ('damage-colour-settings', 0x14139a320, 0x14139a4ee),
    ):
        data = read(start, end - start)
        lines = [f'{i.address:x}: {i.bytes.hex():24} {i.mnemonic} {i.op_str}'
                 for i in disassembler.disasm(data, start)]
        (args.out / (name + '.txt')).write_text('\n'.join(lines) + '\n', encoding='utf-8')
        regions[name] = {'start': hex(start), 'end_exclusive': hex(end),
                         'sha256': hashlib.sha256(data).hexdigest()}
    names = {}
    for va, suffix in ((0x143222a40, 'Armor'), (0x143222a70, 'Health'),
                       (0x143222aa0, 'Vehicle'), (0x143222ad0, 'Friendly')):
        value = read(va, 64).split(b'\0', 1)[0].decode('ascii')
        assert value == f'Ui.HitFeedback.Color.{suffix}.Incoming', (hex(va), value)
        names[hex(va)] = value
    report = {'executable': str(args.exe.resolve()), 'sha256': digest, 'read_only': True,
              'disk_function_state': state, 'existing_f_functions': functions,
              'regions': regions, 'native_colour_names': names,
              'native_test': 'NATIVE TEST PENDING; static bytes do not show live input or rendering'}
    (args.out / 'manifest.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
