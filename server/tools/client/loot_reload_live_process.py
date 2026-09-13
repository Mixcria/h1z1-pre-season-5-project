"""Win32 access for the reviewed August reload and scope branch bytes.

Construction and inspection are read-only. There is no CLI or automatic application.
The caller owns startup-readiness policy and validation of the complete native function.
LiveProcess permits only 77 <-> EB at module RVA 0x11c6ecc and 75 <-> EB at
0x11c6e7c (the F-only outstanding-reload gate), and F6 <-> CE at 0x158cdc6
(the F-only exponential throttle). ScopeProcess permits
its two separately reviewed scope bytes. Each write rechecks process identity and
the expected byte, restores page protection, and flushes the instruction cache.
This is an experimental runtime mechanism; it does not establish gameplay compatibility.
"""

import ctypes
from ctypes import wintypes
from pathlib import Path


PATCH_RVA = 0x11C6ECC
ACK_PATCH_RVA = 0x11C6E7C
THROTTLE_PATCH_RVA = 0x158CDC6
CLIENT_PATH = Path("C:/Aug2017/Client/H1Z1.exe")
READ_ACCESS = 0x410  # PROCESS_QUERY_INFORMATION | PROCESS_VM_READ
WRITE_ACCESS = READ_ACCESS | 0x28  # PROCESS_VM_OPERATION | PROCESS_VM_WRITE
PAGE_EXECUTE_READWRITE = 0x40
STILL_ACTIVE = 259


class LiveProcess:
    # The public constructor remains specific to the reload opcode. The scope
    # helper below declares its own two reviewed sites; no CLI accepts an address.
    ALLOWED_CHANGES = {
        PATCH_RVA: frozenset(((0x77, 0xEB), (0xEB, 0x77))),
        ACK_PATCH_RVA: frozenset(((0x75, 0xEB), (0xEB, 0x75))),
        THROTTLE_PATCH_RVA: frozenset(((0xF6, 0xCE), (0xCE, 0xF6))),
    }

    def __init__(self, pid):
        if not isinstance(pid, int) or not 0 < pid <= 0xFFFFFFFF:
            raise ValueError("PID must be a positive DWORD")
        if ctypes.sizeof(ctypes.c_void_p) != 8:
            raise ValueError("The August client requires a 64-bit Python process")
        self.pid = pid
        self.handle = None
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.psapi = ctypes.WinDLL("psapi", use_last_error=True)
        self._bind()
        self.handle = self.kernel.OpenProcess(READ_ACCESS, False, pid)
        if not self.handle:
            raise self._error("OpenProcess(read)")
        try:
            self.created = self._creation_time(self.handle)
            self.started_unix = self.created / 10_000_000 - 11_644_473_600
            self._require_alive(self.handle)
            modules = (ctypes.c_void_p * 256)()
            needed = wintypes.DWORD()
            if not self.psapi.EnumProcessModulesEx(
                self.handle, modules, ctypes.sizeof(modules), ctypes.byref(needed), 3
            ):
                raise self._error("EnumProcessModulesEx")
            if needed.value < ctypes.sizeof(ctypes.c_void_p) or not modules[0]:
                raise ValueError("Client main module is unavailable")
            self.base = modules[0]
            buffer = ctypes.create_unicode_buffer(32768)
            length = self.psapi.GetModuleFileNameExW(
                self.handle, self.base, buffer, len(buffer)
            )
            if not length or length >= len(buffer) - 1:
                raise self._error("GetModuleFileNameExW")
            self.path = Path(buffer.value).resolve()
            if self.path != CLIENT_PATH.resolve():
                raise ValueError("Target must be exactly C:/Aug2017/Client/H1Z1.exe")
        except BaseException:
            self.close()
            raise

    def _bind(self):
        size_pointer = ctypes.POINTER(ctypes.c_size_t)
        dword_pointer = ctypes.POINTER(wintypes.DWORD)
        time_pointer = ctypes.POINTER(wintypes.FILETIME)
        declarations = [
            (self.kernel.OpenProcess, [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD], wintypes.HANDLE),
            (self.kernel.CloseHandle, [wintypes.HANDLE], wintypes.BOOL),
            (self.kernel.GetProcessTimes,
             [wintypes.HANDLE, time_pointer, time_pointer, time_pointer, time_pointer], wintypes.BOOL),
            (self.kernel.GetExitCodeProcess, [wintypes.HANDLE, dword_pointer], wintypes.BOOL),
            (self.kernel.ReadProcessMemory,
             [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, size_pointer], wintypes.BOOL),
            (self.kernel.WriteProcessMemory,
             [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, size_pointer], wintypes.BOOL),
            (self.kernel.VirtualProtectEx,
             [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_size_t, wintypes.DWORD, dword_pointer], wintypes.BOOL),
            (self.kernel.FlushInstructionCache,
             [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_size_t], wintypes.BOOL),
            (self.psapi.EnumProcessModulesEx,
             [wintypes.HANDLE, ctypes.POINTER(ctypes.c_void_p), wintypes.DWORD,
              dword_pointer, wintypes.DWORD], wintypes.BOOL),
            (self.psapi.GetModuleFileNameExW,
             [wintypes.HANDLE, ctypes.c_void_p, wintypes.LPWSTR, wintypes.DWORD], wintypes.DWORD),
        ]
        for function, arguments, result in declarations:
            function.argtypes = arguments
            function.restype = result

    @staticmethod
    def _error(operation):
        return OSError(ctypes.get_last_error(), operation)

    def _creation_time(self, handle):
        created, exited, kernel, user = (wintypes.FILETIME() for _ in range(4))
        if not self.kernel.GetProcessTimes(
            handle, ctypes.byref(created), ctypes.byref(exited),
            ctypes.byref(kernel), ctypes.byref(user)
        ):
            raise self._error("GetProcessTimes")
        return (created.dwHighDateTime << 32) | created.dwLowDateTime

    def _require_alive(self, handle):
        code = wintypes.DWORD()
        if not self.kernel.GetExitCodeProcess(handle, ctypes.byref(code)):
            raise self._error("GetExitCodeProcess")
        if code.value != STILL_ACTIVE:
            raise OSError(f"Client has exited with code {code.value}")

    def _read(self, handle, address, size):
        if not 0 < size <= 4096 or not 0x10000 <= address < (1 << 47) - size:
            raise ValueError("Invalid bounded process read")
        data = ctypes.create_string_buffer(size)
        count = ctypes.c_size_t()
        if not self.kernel.ReadProcessMemory(
            handle, address, data, size, ctypes.byref(count)
        ) or count.value != size:
            raise self._error(f"ReadProcessMemory({address:#x})")
        return data.raw

    def read(self, address, size):
        if not self.handle:
            raise ValueError("Process handle is closed")
        return self._read(self.handle, address, size)

    def _restore_protection(self, handle, address, protection):
        # A transient restoration failure must not make a later rollback adopt
        # PAGE_EXECUTE_READWRITE as the page's original protection.
        for _ in range(2):
            discarded = wintypes.DWORD()
            if self.kernel.VirtualProtectEx(
                handle, address, 1, protection, ctypes.byref(discarded)
            ):
                return None
        return self._error("VirtualProtectEx(restore)")

    def write_byte(self, address, expected, replacement):
        if (expected, replacement) not in self.ALLOWED_CHANGES.get(address - self.base, ()):
            raise ValueError("Only this helper's reviewed single-byte transitions are permitted")
        if self.read(address, 1) != bytes([expected]):
            raise ValueError("Reload opcode changed before write preparation")
        self._require_alive(self.handle)
        handle = self.kernel.OpenProcess(WRITE_ACCESS, False, self.pid)
        if not handle:
            raise self._error("OpenProcess(write)")
        try:
            if self._creation_time(handle) != self.created:
                raise ValueError("PID now belongs to a different process")
            self._require_alive(handle)
            if self._read(handle, address, 1) != bytes([expected]):
                raise ValueError("Reload opcode changed before write")
            pending = getattr(self, "_pending_protection_restores", None)
            if pending is None:
                pending = self._pending_protection_restores = {}
            # A previous write may have changed the opcode and then failed its
            # cleanup. Recover its saved protection before preparing any new
            # write, including the caller's opcode rollback on this same page.
            for pending_address, protection in tuple(pending.items()):
                error = self._restore_protection(handle, pending_address, protection)
                if error is not None:
                    raise error
                del pending[pending_address]
            old_protection = wintypes.DWORD()
            if not self.kernel.VirtualProtectEx(
                handle, address, 1, PAGE_EXECUTE_READWRITE, ctypes.byref(old_protection)
            ):
                raise self._error("VirtualProtectEx(make writable)")
            pending[address] = old_protection.value
            errors = []
            try:
                value = ctypes.c_ubyte(replacement)
                written = ctypes.c_size_t()
                if not self.kernel.WriteProcessMemory(
                    handle, address, ctypes.byref(value), 1, ctypes.byref(written)
                ) or written.value != 1:
                    errors.append(self._error("WriteProcessMemory(one byte)"))
            finally:
                error = self._restore_protection(handle, address, old_protection.value)
                if error is not None:
                    errors.append(error)
                else:
                    del pending[address]
                if not self.kernel.FlushInstructionCache(handle, address, 1):
                    errors.append(self._error("FlushInstructionCache"))
            if errors:
                raise OSError("; ".join(map(str, errors)))
            if self._read(handle, address, 1) != bytes([replacement]):
                raise ValueError("Reload opcode readback differs from the requested byte")
            return replacement
        finally:
            self.kernel.CloseHandle(handle)

    def close(self):
        if self.handle:
            self.kernel.CloseHandle(self.handle)
            self.handle = None

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()


class ScopeProcess(LiveProcess):
    """Only the two reviewed SecondaryFire branches; never the reload opcode."""

    ALLOWED_CHANGES = {
        0x158BAE1: frozenset(((0x33, 0x41), (0x41, 0x33))),
        0x158BB5E: frozenset(((0x74, 0xEB), (0xEB, 0x74))),
    }

    def patch_lock(self):
        """Serialize whole-function transitions with the C# launcher helper."""
        from contextlib import contextmanager

        @contextmanager
        def locked():
            kernel = self.kernel
            kernel.CreateMutexW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.LPCWSTR]
            kernel.CreateMutexW.restype = wintypes.HANDLE
            kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
            kernel.WaitForSingleObject.restype = wintypes.DWORD
            kernel.ReleaseMutex.argtypes = [wintypes.HANDLE]
            kernel.ReleaseMutex.restype = wintypes.BOOL
            mutex = kernel.CreateMutexW(None, False, 'Local\\Cranberry.BinocularScope.' + str(self.pid))
            if not mutex:
                raise self._error('CreateMutexW(binocular scope)')
            owned = False
            try:
                result = kernel.WaitForSingleObject(mutex, 5000)
                owned = result in (0, 0x80)
                if not owned:
                    raise OSError('Another binocular helper is updating this client')
                yield
            finally:
                if owned:
                    kernel.ReleaseMutex(mutex)
                kernel.CloseHandle(mutex)
        return locked()


class CameraLookProcess(LiveProcess):
    """Only the reviewed HeadLookBlendWeight constant displacement byte."""

    ALLOWED_CHANGES = {0xC697F6: frozenset(((0x8E, 0x7A), (0x7A, 0x8E)))}

    def patch_lock(self):
        from contextlib import contextmanager

        @contextmanager
        def locked():
            kernel = self.kernel
            kernel.CreateMutexW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.LPCWSTR]
            kernel.CreateMutexW.restype = wintypes.HANDLE
            kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
            kernel.WaitForSingleObject.restype = wintypes.DWORD
            kernel.ReleaseMutex.argtypes = [wintypes.HANDLE]
            kernel.ReleaseMutex.restype = wintypes.BOOL
            mutex = kernel.CreateMutexW(None, False, 'Local\\Cranberry.CameraOnlyLook.' + str(self.pid))
            if not mutex:
                raise self._error('CreateMutexW(camera look)')
            owned = False
            try:
                result = kernel.WaitForSingleObject(mutex, 5000)
                owned = result in (0, 0x80)  # signaled or abandoned; both grant ownership
                if not owned:
                    raise OSError('Another camera-look helper is still applying the fix')
                yield
            finally:
                if owned:
                    kernel.ReleaseMutex(mutex)
                kernel.CloseHandle(mutex)
        return locked()
