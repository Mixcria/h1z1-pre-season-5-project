#!/usr/bin/env python3
r"""Read the client's crash-reporter minidumps without a debugger: exception record, x64
registers of the faulting thread, module list, and a return-address scan of the faulting
thread's stack against H1Z1.exe's .pdata function table (via exeview.py).

  minidump.py <dump.dmp> [--exe PATH] [--stack-qwords N] [--mem VA COUNT]

The crash reporter (wws_crashreport_uploader.exe) leaves dumps under
%LOCALAPPDATA%\Temp\SCE\wws_crashreport\<app>-<id>.session\.
"""
import argparse
import bisect
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from exeview import Image, DEFAULT_EXE  # noqa: E402

STREAM_NAMES = {
    3: "ThreadList", 4: "ModuleList", 5: "MemoryList", 6: "Exception", 7: "SystemInfo",
    9: "MemoryInfoList", 15: "MiscInfo", 16: "MemoryInfoList", 17: "ThreadInfoList",
}

# CONTEXT (AMD64) offsets
CTX = {
    "rax": 0x78, "rcx": 0x80, "rdx": 0x88, "rbx": 0x90, "rsp": 0x98, "rbp": 0xA0, "rsi": 0xA8,
    "rdi": 0xB0, "r8": 0xB8, "r9": 0xC0, "r10": 0xC8, "r11": 0xD0, "r12": 0xD8, "r13": 0xE0,
    "r14": 0xE8, "r15": 0xF0, "rip": 0xF8,
}


class Dump:
    def __init__(self, path):
        with open(path, "rb") as f:
            self.d = f.read()
        d = self.d
        assert d[:4] == b"MDMP", "not a minidump"
        nstreams, rva = struct.unpack_from("<II", d, 8)
        self.streams = {}
        for i in range(nstreams):
            stype, size, srva = struct.unpack_from("<III", d, rva + i * 12)
            self.streams.setdefault(stype, []).append((srva, size))
        self.memory = []  # (start, size, rva)
        for srva, _ in self.streams.get(5, []):
            n = struct.unpack_from("<I", d, srva)[0]
            for i in range(n):
                self.memory.append(self._mem_desc(srva + 4 + i * 16))
        self.memory.sort()

    def _mem_desc(self, at):
        start = struct.unpack_from("<Q", self.d, at)[0]
        size, rva = struct.unpack_from("<II", self.d, at + 8)
        return start, size, rva

    def read(self, va, size):
        for start, msize, rva in self.memory:
            if start <= va and va + size <= start + msize:
                off = rva + (va - start)
                return self.d[off:off + size]
        return None

    def modules(self):
        out = []
        for srva, _ in self.streams.get(4, []):
            n = struct.unpack_from("<I", self.d, srva)[0]
            for i in range(n):
                at = srva + 4 + i * 108
                base, size, _cs, _ts, name_rva = struct.unpack_from("<QIIII", self.d, at)
                out.append((base, size, self.string(name_rva)))
        return out

    def string(self, rva):
        n = struct.unpack_from("<I", self.d, rva)[0]
        return self.d[rva + 4:rva + 4 + n].decode("utf-16-le", "replace")

    def threads(self):
        out = []
        for srva, _ in self.streams.get(3, []):
            n = struct.unpack_from("<I", self.d, srva)[0]
            for i in range(n):
                at = srva + 4 + i * 48
                tid, _sc, _pc, _pr, teb, sstart = struct.unpack_from("<IIIIQQ", self.d, at)
                ssize, srva2 = struct.unpack_from("<II", self.d, at + 32)
                csize, crva = struct.unpack_from("<II", self.d, at + 40)
                out.append(dict(tid=tid, teb=teb, stack=(sstart, ssize, srva2), context=(csize, crva)))
        return out

    def exception(self):
        for srva, _ in self.streams.get(6, []):
            tid = struct.unpack_from("<I", self.d, srva)[0]
            code, flags, rec, addr, nparams = struct.unpack_from("<IIQQI", self.d, srva + 8)
            params = struct.unpack_from("<15Q", self.d, srva + 8 + 32)
            csize, crva = struct.unpack_from("<II", self.d, srva + 8 + 152)
            return dict(tid=tid, code=code, flags=flags, address=addr, nparams=nparams,
                        params=params[:nparams], context=(csize, crva))
        return None

    def regs(self, ctx):
        csize, crva = ctx
        return {k: struct.unpack_from("<Q", self.d, crva + off)[0] for k, off in CTX.items()}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dump")
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--stack-qwords", type=int, default=400)
    ap.add_argument("--mem", nargs=2, action="append", default=[], metavar=("VA", "COUNT"))
    a = ap.parse_args()

    dump = Dump(a.dump)
    img = Image(a.exe)
    print("streams:", ", ".join(f"{STREAM_NAMES.get(t, t)}" for t in sorted(dump.streams)))
    mods = dump.modules()
    exe = next((m for m in mods if m[2].lower().endswith("h1z1.exe")), None)
    print("modules:", len(mods))
    for base, size, name in mods[:12]:
        print(f"  {base:#x} +{size:#x} {name}")
    if exe is None:
        print("H1Z1.exe not in module list")
        return
    base = exe[0]
    delta = base - img.image_base
    print(f"H1Z1.exe base {base:#x} (static delta {delta:+#x})")

    def sym(va):
        s = va - delta
        f = img.func(s)
        if f:
            return f"{s:#x} in FUN_{f[0]:x} (+{s - f[0]:#x})"
        return f"{s:#x}"

    ex = dump.exception()
    if ex:
        print(f"\nexception: code {ex['code']:#x} flags {ex['flags']:#x} address {sym(ex['address'])} thread {ex['tid']:#x}")
        if ex["nparams"]:
            print("  params:", ", ".join(f"{p:#x}" for p in ex["params"]))
        regs = dump.regs(ex["context"])
        for row in (("rip", "rsp", "rbp"), ("rax", "rbx", "rcx", "rdx"), ("rsi", "rdi", "r8", "r9"),
                    ("r10", "r11", "r12", "r13"), ("r14", "r15")):
            print("  " + "  ".join(f"{r}={regs[r]:#018x}" for r in row))
        print(f"  rip = {sym(regs['rip'])}")
        thread = next((t for t in dump.threads() if t["tid"] == ex["tid"]), None)
        if thread:
            sstart, ssize, srva = thread["stack"]
            print(f"\nstack {sstart:#x}..{sstart + ssize:#x} ({ssize:#x} bytes captured)")
            rsp = regs["rsp"]
            text = [s for s in img.sections if s[0] == ".text"][0]
            tlo, thi = img.image_base + text[1], img.image_base + text[1] + text[2]
            print("return-address scan from rsp (static addresses, .pdata function + offset):")
            for i in range(a.stack_qwords):
                va = rsp + i * 8
                raw = dump.read(va, 8)
                if raw is None:
                    break
                q = struct.unpack("<Q", raw)[0]
                s = q - delta
                if tlo <= s < thi:
                    f = img.func(s)
                    tag = f"FUN_{f[0]:x}+{s - f[0]:#x}" if f else "?"
                    # a genuine return address is preceded by a call
                    prev = dump.read(q - 5, 5)
                    iscall = prev is not None and (prev[0] == 0xE8 or prev[3] == 0xFF or prev[2] == 0xFF)
                    print(f"  [rsp+{i * 8:#06x}] {s:#x}  {tag}{'  (call)' if iscall else ''}")
    for va, count in a.mem:
        va, count = int(va, 0), int(count, 0)
        raw = dump.read(va, count)
        print(f"\nmemory {va:#x} x{count}: {'<not captured>' if raw is None else raw.hex()}")


if __name__ == "__main__":
    main()
