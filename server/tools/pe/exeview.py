#!/usr/bin/env python3
r"""Read-only views of a PE image without Ghidra: sections, VA<->file offset, qwords at a VA,
strings, data pointers to a VA (vtable slots / tables), RIP-relative code references,
call/jmp sites (thunk resolution), .pdata function bounds and RTTI-aware vtable dumps.

  exeview.py [--exe PATH] sections
  exeview.py va2off 0x14xxxxxxx | off2va N
  exeview.py qwords 0x14xxxxxxx [count]        # pointers stored at VA
  exeview.py findstr TEXT                       # ASCII string occurrences -> VA
  exeview.py ptrs 0x14xxxxxxx                   # 8-byte pointers to VA in data (vtable slots)
  exeview.py xrefs 0x14xxxxxxx                  # RIP-relative disp32 references (lea/mov) to VA
  exeview.py calls 0x14xxxxxxx                  # E8 call / E9 jmp sites targeting VA (thunks)
  exeview.py func 0x14xxxxxxx                   # .pdata function containing VA
  exeview.py slots 0x14xxxxxxx                  # vtable slots holding VA or one of its thunks
  exeview.py vtable 0x14xxxxxxx                 # RTTI class name + every slot (thunks resolved)
  exeview.py bytes 0x14xxxxxxx [count]          # hex dump
Default exe: C:\Aug2017\Client\H1Z1.exe (build 0.0.118.208059).
"""
import bisect
import struct
import sys

DEFAULT_EXE = r"C:\Aug2017\Client\H1Z1.exe"


class Image:
    def __init__(self, path):
        with open(path, "rb") as f:
            self.data = f.read()
        d = self.data
        pe = struct.unpack_from("<I", d, 0x3C)[0]
        assert d[pe:pe + 4] == b"PE\0\0", "not a PE file"
        nsec = struct.unpack_from("<H", d, pe + 6)[0]
        optsize = struct.unpack_from("<H", d, pe + 20)[0]
        opt = pe + 24
        assert struct.unpack_from("<H", d, opt)[0] == 0x20B, "not PE32+"
        self.image_base = struct.unpack_from("<Q", d, opt + 24)[0]
        self.sections = []
        at = opt + optsize
        for _ in range(nsec):
            name = d[at:at + 8].rstrip(b"\0").decode("ascii", "replace")
            vsize, va, rawsize, rawptr = struct.unpack_from("<IIII", d, at + 8)
            chars = struct.unpack_from("<I", d, at + 36)[0]
            self.sections.append((name, va, vsize, rawptr, rawsize, chars))
            at += 40
        self._pdata = None

    def va2off(self, va):
        rva = va - self.image_base
        for name, sva, vsize, rawptr, rawsize, _ in self.sections:
            if sva <= rva < sva + max(vsize, rawsize):
                return rawptr + (rva - sva) if rva - sva < rawsize else None
        return None

    def off2va(self, off):
        for name, sva, vsize, rawptr, rawsize, _ in self.sections:
            if rawptr <= off < rawptr + rawsize:
                return self.image_base + sva + (off - rawptr)
        return None

    def section_of(self, va):
        rva = va - self.image_base
        for s in self.sections:
            if s[1] <= rva < s[1] + max(s[2], s[4]):
                return s
        return None

    def is_code(self, va):
        s = self.section_of(va)
        return s is not None and bool(s[5] & 0x20000000)

    def qword(self, va):
        off = self.va2off(va)
        return None if off is None else struct.unpack_from("<Q", self.data, off)[0]

    def dword(self, va):
        off = self.va2off(va)
        return None if off is None else struct.unpack_from("<I", self.data, off)[0]

    def cstring(self, va, limit=200):
        off = self.va2off(va)
        if off is None:
            return None
        e = self.data.find(b"\0", off, off + limit)
        return self.data[off:e if e >= 0 else off + limit].decode("ascii", "replace")

    # --- .pdata ---------------------------------------------------------------
    def pdata(self):
        if self._pdata is None:
            starts, ends = [], []
            for name, sva, vsize, rawptr, rawsize, _ in self.sections:
                if name != ".pdata":
                    continue
                for i in range(rawptr, rawptr + rawsize - 11, 12):
                    b, e, u = struct.unpack_from("<III", self.data, i)
                    if b == 0:
                        break
                    starts.append(b)
                    ends.append(e)
            self._pdata = (starts, ends)
        return self._pdata

    def func(self, va):
        """(start, end) of the .pdata function containing va, or None."""
        starts, ends = self.pdata()
        rva = va - self.image_base
        i = bisect.bisect_right(starts, rva) - 1
        if i >= 0 and starts[i] <= rva < ends[i]:
            return self.image_base + starts[i], self.image_base + ends[i]
        return None

    # --- code scans -----------------------------------------------------------
    def exec_ranges(self):
        for name, sva, vsize, rawptr, rawsize, chars in self.sections:
            if chars & 0x20000000:
                yield self.image_base + sva, rawptr, rawsize

    def calls_to(self, target):
        """E8 (call) / E9 (jmp) sites whose rel32 target is `target`."""
        d = self.data
        out = []
        for base, rawptr, rawsize in self.exec_ranges():
            end = rawptr + rawsize - 5
            for opcode, kind in ((b"\xe8", "call"), (b"\xe9", "jmp")):
                i = rawptr
                while True:
                    k = d.find(opcode, i, end)
                    if k < 0:
                        break
                    rel = struct.unpack_from("<i", d, k + 1)[0]
                    site = base + (k - rawptr)
                    if site + 5 + rel == target:
                        out.append((site, kind))
                    i = k + 1
        out.sort()
        return out

    def thunks_of(self, target):
        return [site for site, kind in self.calls_to(target) if kind == "jmp"]

    def resolve_thunk(self, va):
        """Follow E9 jump thunks to the real code."""
        for _ in range(4):
            off = self.va2off(va)
            if off is None or self.data[off] != 0xE9:
                return va
            va = va + 5 + struct.unpack_from("<i", self.data, off + 1)[0]
        return va

    def ptrs_to(self, target):
        pat = struct.pack("<Q", target)
        out, start = [], 0
        while True:
            i = self.data.find(pat, start)
            if i < 0:
                return out
            if i % 8 == 0:
                out.append(self.off2va(i))
            start = i + 1

    # --- RTTI -----------------------------------------------------------------
    def col_at(self, va):
        """If qword(va) points to an RTTI Complete Object Locator, return its VA."""
        q = self.qword(va)
        if q is None or self.is_code(q) or self.section_of(q) is None:
            return None
        fields = [self.dword(q + 4 * i) for i in range(6)]
        if None in fields:
            return None
        sig, off, cd, td, chd, self_rva = fields
        if sig == 1 and self_rva == q - self.image_base:
            return q
        return None

    def class_name(self, col):
        td = self.image_base + self.dword(col + 12)
        return self.cstring(td + 16)

    def bases(self, col):
        chd = self.image_base + self.dword(col + 16)
        n = self.dword(chd + 8)
        arr = self.image_base + self.dword(chd + 12)
        names = []
        for i in range(min(n, 16)):
            bcd = self.image_base + self.dword(arr + 4 * i)
            td = self.image_base + self.dword(bcd)
            names.append(self.cstring(td + 16))
        return names

    def vtable_base(self, slot):
        for k in range(0, 200):
            at = slot - 8 * k
            if self.col_at(at - 8):
                return at
            q = self.qword(at)
            if q is None or not self.is_code(q):
                return None
        return None


def main(argv):
    exe = DEFAULT_EXE
    if len(argv) > 1 and argv[0] == "--exe":
        exe, argv = argv[1], argv[2:]
    if not argv:
        print(__doc__)
        return 1
    img = Image(exe)
    cmd, args = argv[0], argv[1:]

    def fn(va):
        f = img.func(va)
        return "in %#x..%#x" % f if f else "in ?"

    if cmd == "sections":
        print("image base %#x" % img.image_base)
        for name, va, vsize, rawptr, rawsize, chars in img.sections:
            print("%-8s va %#x vsize %#x raw %#x+%#x chars %#010x" % (name, img.image_base + va, vsize, rawptr, rawsize, chars))
    elif cmd == "va2off":
        print(img.va2off(int(args[0], 16)))
    elif cmd == "off2va":
        print("%#x" % img.off2va(int(args[0], 0)))
    elif cmd == "qwords":
        va = int(args[0], 16)
        for i in range(int(args[1]) if len(args) > 1 else 16):
            q = img.qword(va + 8 * i)
            sec = img.section_of(q) if q is not None else None
            print("%#x  [+%#04x]  %#018x  %s" % (va + 8 * i, 8 * i, q, sec[0] if sec else ""))
    elif cmd == "findstr":
        needle = args[0].encode("utf-8")
        start = 0
        while True:
            i = img.data.find(needle, start)
            if i < 0:
                break
            b = i
            while b > 0 and img.data[b - 1] != 0:
                b -= 1
            e = img.data.find(b"\0", i)
            print("%#x (file %#x) %r" % (img.off2va(b), b, img.data[b:e][:120]))
            start = i + 1
    elif cmd == "ptrs":
        for va in img.ptrs_to(int(args[0], 16)):
            sec = img.section_of(va)
            print("%#x in %s" % (va, sec[0] if sec else "?"))
    elif cmd == "xrefs":
        target = int(args[0], 16)
        d = img.data
        for base, rawptr, rawsize in img.exec_ranges():
            end = rawptr + rawsize
            for i in range(rawptr, end - 6):
                m = d[i + 1]
                if (m & 0xC7) == 0x05:
                    disp = struct.unpack_from("<i", d, i + 2)[0]
                    site = base + (i - rawptr)
                    if site + 6 + disp == target:
                        print("%#x  op %02x modrm %02x  %s" % (site, d[i], m, fn(site)))
    elif cmd == "calls":
        for site, kind in img.calls_to(int(args[0], 16)):
            print("%#x  %s  %s" % (site, kind, fn(site)))
    elif cmd == "func":
        f = img.func(int(args[0], 16))
        print("%#x..%#x" % f if f else "not in .pdata")
    elif cmd == "slots":
        target = int(args[0], 16)
        cands = [target] + img.thunks_of(target)
        for c in cands:
            for slot in img.ptrs_to(c):
                tag = " (thunk)" if c != target else ""
                base = img.vtable_base(slot)
                if base is None:
                    print("%#x holds %#x%s  (no RTTI vtable found)" % (slot, c, tag))
                    continue
                col = img.col_at(base - 8)
                print("%#x holds %#x%s  = %s vtable %#x slot +%#x (index %d)" % (
                    slot, c, tag, img.class_name(col), base, slot - base, (slot - base) // 8))
    elif cmd == "vtable":
        va = int(args[0], 16)
        base = va if img.col_at(va - 8) else img.vtable_base(va)
        if base is None:
            print("no RTTI complete object locator before this address")
            return 1
        col = img.col_at(base - 8)
        print("vtable %#x  class %s  bases %s" % (base, img.class_name(col), img.bases(col)))
        i = 0
        while True:
            at = base + 8 * i
            q = img.qword(at)
            if q is None or not img.is_code(q) or (i > 0 and img.col_at(at)):
                break
            real = img.resolve_thunk(q)
            print("  [+%#04x] %#x%s" % (8 * i, q, (" -> %#x" % real) if real != q else ""))
            i += 1
    elif cmd == "vtable-raw":
        # A table without RTTI: walk back over code pointers to the first one, then list
        # forward until the pointers stop being code. Each slot: thunk target + function bounds.
        va = int(args[0], 16)
        base = va
        while img.is_code(img.qword(base - 8) or 0):
            base -= 8
        print("raw vtable %#x (no RTTI; first code pointer after %#x)" % (base, base - 8))
        i = 0
        while True:
            at = base + 8 * i
            q = img.qword(at)
            if q is None or not img.is_code(q):
                break
            real = img.resolve_thunk(q)
            print("  [+%#04x] %#x%s  %s" % (8 * i, q, (" -> %#x" % real) if real != q else "", fn(real)))
            i += 1
    elif cmd == "bytes":
        va = int(args[0], 16)
        off = img.va2off(va)
        print(img.data[off:off + (int(args[1]) if len(args) > 1 else 64)].hex(" "))
    else:
        print(__doc__)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
