"""Read-only August reticle/camera/live-weapon trace; no injected calls or memory writes.

Usage: python read_reticle_state.py PID OUTPUT.jsonl [--seconds 180]

Native provenance (build 0.0.118.208059):
* 140bfe350 / 14146ad70: legacy reticle type +8, alternate +c, melee +79.
* 14148cbb0: BaseClient +321a0 controller; vtable +188 selects primary reticle.
* 140e3e900: first-person predicate returns 1; 140e3e910 reads controller +256.
* 1411b8d00: local player +5520/+5521 and +5528/+5529 reticle obstruction state.
* 1411bbbf0 -> 140c4ae00 / 140dbc370: active slot and local inventory lookup.
* Weapon vtable 143254c98 +40 -> 141487c50 returns this; component is item +a8.
* 14228d970: component group/mode indices and nested descriptors.
* 14147f040: live fire-mode definition lookup; columns are WeaponListLayouts.cs.

Samples are asynchronous observations, not atomic snapshots. Raw flags deliberately
retain their offsets rather than assuming an unproven meaning such as "airborne".
The inventory resolver follows the on-foot path, not a mounted vehicle weapon.
"""

import argparse
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import json
from pathlib import Path
import struct
import time


def arguments():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pid", type=int)
    parser.add_argument("output", type=Path)
    parser.add_argument("--seconds", type=float, default=180)
    args = parser.parse_args()
    if args.pid <= 0 or not 0 < args.seconds <= 180:
        parser.error("PID must be positive and duration must be in (0, 180] seconds")
    return args


def timestamp():
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds")


def address(value):
    return hex(value) if value else None


class Reader:
    def __init__(self, pid):
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.psapi = ctypes.WinDLL("psapi", use_last_error=True)
        self.kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self.kernel.OpenProcess.restype = wintypes.HANDLE
        self.kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        self.kernel.ReadProcessMemory.argtypes = [
            wintypes.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t,
            ctypes.POINTER(ctypes.c_size_t),
        ]
        self.psapi.EnumProcessModulesEx.argtypes = [
            wintypes.HANDLE, ctypes.POINTER(ctypes.c_void_p), wintypes.DWORD,
            ctypes.POINTER(wintypes.DWORD), wintypes.DWORD,
        ]
        self.psapi.GetModuleFileNameExW.argtypes = [
            wintypes.HANDLE, ctypes.c_void_p, wintypes.LPWSTR, wintypes.DWORD,
        ]
        self.handle = self.kernel.OpenProcess(0x410, False, pid)  # QUERY_INFORMATION | VM_READ
        if not self.handle:
            raise OSError(ctypes.get_last_error(), "OpenProcess")
        try:
            modules = (ctypes.c_void_p * 256)()
            needed = wintypes.DWORD()
            if not self.psapi.EnumProcessModulesEx(
                self.handle, modules, ctypes.sizeof(modules), ctypes.byref(needed), 3
            ):
                raise OSError(ctypes.get_last_error(), "EnumProcessModulesEx")
            self.base = modules[0]
            path = ctypes.create_unicode_buffer(1024)
            if not self.psapi.GetModuleFileNameExW(self.handle, self.base, path, len(path)):
                raise OSError(ctypes.get_last_error(), "GetModuleFileNameExW")
            self.path = Path(path.value)
            if self.path.resolve() != Path("C:/Aug2017/Client/H1Z1.exe").resolve():
                raise ValueError("Target must be exactly C:/Aug2017/Client/H1Z1.exe")
        except BaseException:
            self.close()
            raise

    def close(self):
        self.kernel.CloseHandle(self.handle)

    def read(self, location, size):
        if not 0 <= size <= 4096 or not 0x10000 <= location < (1 << 47) - size:
            raise ValueError("Invalid bounded read")
        data = ctypes.create_string_buffer(size)
        count = ctypes.c_size_t()
        if not self.kernel.ReadProcessMemory(
            self.handle, location, data, size, ctypes.byref(count)
        ) or count.value != size:
            raise OSError(ctypes.get_last_error(), hex(location))
        return data.raw

    def u64(self, location):
        return struct.unpack("<Q", self.read(location, 8))[0]

    def u32(self, location):
        return struct.unpack("<I", self.read(location, 4))[0]

    def lookup(self, owner, count_offset, buckets_offset, key, key_offset, next_offset, wide=False):
        count = self.u32(owner + count_offset)
        if not count:
            return 0
        if count > 65536 or count & (count - 1):
            raise ValueError("Unexpected native hash count")
        hash_key = key ^ (key >> 32) if wide else key
        node = self.u64(self.u64(owner + buckets_offset) + (hash_key & (count - 1)) * 8)
        seen = set()
        for _ in range(128):
            if not node:
                return 0
            if node in seen:
                raise ValueError("Native hash changed or cycled")
            seen.add(node)
            if (self.u64 if wide else self.u32)(node + key_offset) == key:
                return node
            node = self.u64(node + next_offset)
        raise ValueError("Native hash traversal limit")

    def weapon_snapshot(self):
        manager = self.u64(self.base + 0x3f69f60)
        slots = self.u64(self.base + 0x3f69ba0)
        if not manager or not slots:
            return {}
        active = self.u32(slots + 0x228)
        result = {"activeSlot": active}
        if self.u32(self.base + 0x3d969bc):
            owner = self.u64(manager + 0xdaa8)
        else:
            owner = self.lookup(manager, 0xdaa0, 0xdac0, self.u32(manager + 0x3a8), 0x2d0, 0x2d8)
        if not owner:
            return result
        node = self.u64(owner + 0xb0 + (active % 31) * 8)
        seen = set()
        guid = 0
        for _ in range(128):
            if not node:
                break
            if node in seen:
                raise ValueError("Native slots changed or cycled")
            seen.add(node)
            if self.u32(node + 0xc0) == active:
                guid = self.u64(node + 0x28)
                break
            node = self.u64(node + 0xc8)
        else:
            raise ValueError("Native slot traversal limit")
        if not guid:
            return result
        item = self.lookup(manager, 0xbde8, 0xbe08, guid, 0x88, 0x90, wide=True)
        result.update(weaponGuid=guid, weaponItem=address(item))
        if not item:
            return result
        vtable = self.u64(item)
        result["weaponVtable"] = address(vtable)
        if vtable != self.base + 0x3254c98:
            return result
        component = item + 0xa8
        group_index = self.u32(component + 0x64)
        mode_index = self.u32(component + 0x68)
        result.update(groupIndex=group_index, modeIndex=mode_index,
                      useSecondaryReticle=self.read(item + 0x290, 1).hex())
        if group_index >= min(32, self.u32(component + 0x38)):
            return result
        group = self.u64(component + 0x30) + group_index * 32
        if mode_index >= min(32, self.u32(group + 0x10)):
            return result
        descriptor = self.u64(group + 8) + mode_index * 40
        mode_id = self.u32(descriptor + 8)
        result.update(modeDefinitionId=mode_id, modeResolver=address(self.u64(descriptor)))
        table = self.u64(self.base + 0x3f6a030)
        record = self.lookup(table, 0xa0, 0xc0, mode_id, 0x378, 0x380) if table else 0
        if record:
            result.update(
                liveModeRecord=address(record), liveModeType=self.read(record + 0x24, 1)[0],
                liveModeFlags=self.read(record + 0x20, 3).hex(),
                liveModePrimaryReticle=self.u32(record + 0x138),
                liveModeSecondaryReticle=self.u32(record + 0x13c),
                liveModeForceFp=self.read(record + 0x2dc, 1)[0],
                liveModeFpFov=struct.unpack("<f", self.read(record + 0x2d0, 4))[0],
            )
        return result

    def snapshot(self):
        result = {}
        try:
            client = self.u64(self.base + 0x3f696a0)
            reticle = self.u64(self.base + 0x3f6a200)
            manager = self.u64(self.base + 0x3f69430)
            result.update(client=address(client), reticle=address(reticle))
            if reticle:
                result.update(reticleType=self.u32(reticle + 8),
                              reticleAlternateType=self.u32(reticle + 0xc),
                              reticleMelee=self.read(reticle + 0x79, 1).hex())
            if client:
                controller = self.u64(client + 0x321a0)
                result["controller"] = address(controller)
                if controller:
                    vtable = self.u64(controller)
                    result.update(controllerVtable=address(vtable),
                                  fpPredicate=address(self.u64(vtable + 0x188)))
                    if vtable in (self.base + 0x315a458, self.base + 0x315aec0):
                        result["controllerFlag256"] = self.read(controller + 0x256, 1).hex()
            if manager:
                player = self.u64(manager + 0x1948)
                result["player"] = address(player)
                if player:
                    vtable = self.u64(player)
                    result["playerVtable"] = address(vtable)
                    if vtable == self.base + 0x31dddc0:
                        result.update(movementFlags=self.read(player + 0x37e0, 16).hex(),
                                      reticleFlags=self.read(player + 0x5520, 16).hex())
                        # 1411acc50 (player vtable +5f0) returns this controller.
                        player_controller = self.u64(player + 0x45b8)
                        result["playerController"] = address(player_controller)
                        if player_controller:
                            player_controller_vtable = self.u64(player_controller)
                            result["playerControllerVtable"] = address(player_controller_vtable)
                            if player_controller_vtable in (
                                self.base + 0x315a458, self.base + 0x315aec0
                            ):
                                result["playerControllerFlag256"] = self.read(
                                    player_controller + 0x256, 1
                                ).hex()
            result.update(self.weapon_snapshot())
        except (OSError, ValueError, struct.error) as error:
            # Do not continue sampling an exited process. Transient object reads are logged.
            self.u64(self.base + 0x3f696a0)
            result["error"] = str(error)
        return result


def main():
    args = arguments()
    reader = Reader(args.pid)
    try:
        with args.output.open("x", encoding="utf-8") as output:
            def emit(value):
                output.write(json.dumps(value) + "\n")
                output.flush()
            emit({"pid": args.pid, "module": str(reader.path), "base": address(reader.base),
                  "readOnly": True, "durationSeconds": args.seconds})
            start = time.monotonic()
            previous = None
            last_emitted = 0
            while time.monotonic() - start < args.seconds:
                try:
                    state = reader.snapshot()
                except (OSError, ValueError):
                    emit({"timeUtc": timestamp(), "ended": "Target exited or no longer readable"})
                    return
                now = time.monotonic()
                if state != previous or now - last_emitted >= 5:
                    emit({"timeUtc": timestamp(), "elapsed": round(now - start, 3), **state})
                    previous = state
                    last_emitted = now
                time.sleep(0.04)
            emit({"timeUtc": timestamp(), "ended": "Duration reached"})
    finally:
        reader.close()


if __name__ == "__main__":
    main()
