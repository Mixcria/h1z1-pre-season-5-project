"""Read only known August entity/render fields; never invoke client functions or write memory.

Usage: python read_vehicle_visibility.py PID TRANSIENT_ID [TRANSIENT_ID ...]
"""
import ctypes
from ctypes import wintypes
import json
import math
from pathlib import Path
import struct
import sys

k = ctypes.WinDLL('kernel32', use_last_error=True)
p = ctypes.WinDLL('psapi', use_last_error=True)
k.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
k.OpenProcess.restype = wintypes.HANDLE
k.CloseHandle.argtypes = [wintypes.HANDLE]
k.ReadProcessMemory.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
p.EnumProcessModulesEx.argtypes = [wintypes.HANDLE, ctypes.POINTER(ctypes.c_void_p), wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), wintypes.DWORD]
p.GetModuleFileNameExW.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.LPWSTR, wintypes.DWORD]
pid = int(sys.argv[1])
h = k.OpenProcess(0x410, False, pid)  # QUERY_INFORMATION | VM_READ, no write permissions
if not h:
    raise OSError(ctypes.get_last_error(), 'OpenProcess')

def read(address, size):
    if not 0 <= size <= 4096 or not 0x10000 <= address < (1 << 47) - size:
        raise ValueError('Unreasonable bounded read')
    data = ctypes.create_string_buffer(size)
    count = ctypes.c_size_t()
    if not k.ReadProcessMemory(h, address, data, size, ctypes.byref(count)) or count.value != size:
        raise OSError(ctypes.get_last_error(), hex(address))
    return data.raw

def val(address, fmt):
    return struct.unpack(fmt, read(address, struct.calcsize(fmt)))

def u64(address):
    return val(address, '<Q')[0]

def u32(address):
    return val(address, '<I')[0]

def controller_snapshot(entity):
    controller = u64(entity + 0x5b8 + entity % 3 * 8) ^ entity
    result = {'controller': hex(controller)}
    if controller:
        matrix = val(controller + 0xa0, '<16f')
        compact = val(controller + 0xe0, '<12f')
        dirty = read(controller + 0x130, 1)[0]
        result.update({'controllerVtable': hex(u64(controller)),
                       'matrixCache': matrix,
                       'matrixCachePosition': matrix[12:16],
                       'compactTransform': compact,
                       'matrixDirtyFlags': hex(dirty),
                       'actualPosition': compact[:4] if dirty & 1 else matrix[12:16],
                       'actualScale': compact[8:11] if dirty & 1 else tuple(
                           math.sqrt(sum(matrix[r*4+c]**2 for c in range(3))) for r in range(3)),
                       'positionSource': 'compact' if dirty & 1 else 'matrix-cache'})
        if u64(controller) == base + 0x312b9a0:
            result['movementQueueCount'] = u32(controller + 0x1a8)
            result['movementQueueIndex'] = u32(controller + 0x1ac)
    return result

try:
    modules = (ctypes.c_void_p * 256)()
    needed = wintypes.DWORD()
    if not p.EnumProcessModulesEx(h, modules, ctypes.sizeof(modules), ctypes.byref(needed), 3):
        raise OSError(ctypes.get_last_error(), 'EnumProcessModulesEx')
    base = modules[0]
    path = ctypes.create_unicode_buffer(1024)
    if not p.GetModuleFileNameExW(h, base, path, len(path)):
        raise OSError(ctypes.get_last_error(), 'GetModuleFileNameExW')
    expected_path = Path('C:/Aug2017/Client/H1Z1.exe').resolve()
    if Path(path.value).resolve() != expected_path:
        raise ValueError('Target must be exactly C:/Aug2017/Client/H1Z1.exe; no native offsets read')
    manager = u64(base + 0x3f69430)
    print(json.dumps({'pid': pid, 'module': path.value, 'base': hex(base), 'manager': hex(manager)}))
    try:
        player = u64(manager + 0x1948)
        if player:
            print(json.dumps({'playerEntity': hex(player), **controller_snapshot(player)}))
    except (OSError, ValueError, struct.error) as error:
        print(json.dumps({'playerReadError': str(error)}))
    transients = list(map(int, sys.argv[2:]))
    if len(transients) > 150 or any(not 2000000 <= transient <= 2999999 for transient in transients):
        raise ValueError('At most 150 known vehicle transients in the 2000000..2999999 range')
    for transient in transients:
        try:
            entity = u64(manager + 0x398 + transient % 100 * 8)
            seen = set()
            while entity and u32(entity + 0x3b8) != transient:
                if entity in seen or len(seen) >= 256:
                    raise ValueError('Entity bucket changed or exceeded bounded traversal')
                seen.add(entity)
                entity = u64(entity + 0x3c0)
            if not entity:
                print(json.dumps({'transient': transient, 'found': False}))
                continue
            vtable = u64(entity)
            if vtable != base + 0x315e068:
                print(json.dumps({'transient': transient, 'found': False,
                                  'rejectedEntity': hex(entity), 'vtable': hex(vtable),
                                  'reason': 'Not the verified August vehicle class; no vehicle offsets read'}))
                continue
            actor = u64(entity + 0x590)
            result = {'transient': transient, 'found': True, 'entity': hex(entity), 'vtable': hex(vtable),
                      'model': u32(entity + 0x18c8), 'positionUpdateType': u32(entity + 0x382c),
                      'entityFlags37e0': read(entity + 0x37e0, 16).hex(), 'actor': hex(actor),
                      'lastSetPosition_e90': val(entity + 0xe90, '<4f'),
                      'distanceOverride_b30': val(entity + 0xb30, '<f')[0],
                      **controller_snapshot(entity),
                      'owner': u64(entity + 0x45b0), 'vehicleFlags43f0': read(entity + 0x43f0, 2).hex(),
                      'shaderVectors': val(entity + 0x6300, '<8f')}
            if actor:
                lod = read(actor + 0x328, 1)[0]
                count = u32(actor + 0x220)
                result.update({'actorFlags28': read(actor + 0x28, 1).hex(),
                               'actorFlags578': read(actor + 0x578, 5).hex(),
                               'lodIndex': lod, 'lodCount': count,
                               'actorDistance_5a8': val(actor + 0x5a8, '<f')[0],
                               'lodPresent': bool(u64(u64(actor + 0x218) + lod * 8)) if lod < min(count, 64) else False,
                               'transform': hex(u64(actor + 0x360)), 'physics': hex(u64(actor + 0x408))})
                transform = u64(actor + 0x360)
                if transform:
                    result['transformVtable'] = hex(u64(transform))
                    result['transformWords20_80'] = val(transform + 0x20, '<24f')
            print(json.dumps(result))
        except (OSError, ValueError, struct.error) as error:
            print(json.dumps({'transient': transient, 'readError': str(error)}))

finally:
    k.CloseHandle(h)
