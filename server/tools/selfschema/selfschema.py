#!/usr/bin/env python3
"""
selfschema.py - extract the ordered stream-read sequence of a Ghidra-decompiled loader tree.

Input : directories of Ghidra C dumps (one function per file, `FUN_<addr>_<addr>.c`, as written
        by tools/ghidra/DumpClassMethods.java) and a root function.
Output: for every reachable loader, the ordered list of stream reads (inline fixed-width reads,
        the string / varint / counted-bytes helpers, calls into sub-loaders, counted loops and
        conditionals), plus an evaluation of the "minimal record" path where every value read
        from the stream is zero. Anything the evaluator cannot decide is reported for review, and
        a self-check compares the number of cursor advances in the C with the reads extracted.

The client's stream object is `+0x00 base, +0x08 length, +0x10 cursor, +0x18 end, +0x20 flag`.
Ghidra renders it either as `param_N + 0x10/0x18/0x20` (longlong-typed parameter) or as
`param_N + 4/6/8` (int*-typed parameter); both forms are recognised.

Usage:
  python selfschema.py <dumpdir> [<dumpdir> ...] --root FUN_140a31140 [--md out.md] [--json out.json]
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
from dataclasses import dataclass, field
from typing import Optional

# ----------------------------------------------------------------------------------------------
# Known stream primitives (address -> (stream parameter index, kind)).  Verified from the dumps:
#   FUN_140b78f60(stream, dest)  i32 length + bytes into a SoeUtil string         -> "str"
#   FUN_140a190f0(stream, dest)  varint: low 2 bits of byte 0 = extra byte count  -> "varint"
#   FUN_140a4c4c0(stream, dest)  u32 length + bytes into an Array<u8>             -> "bytes"
# ----------------------------------------------------------------------------------------------
PRIMITIVES = {
    "FUN_140b78f60": (0, "str"),
    "FUN_140a190f0": (0, "varint"),
    "FUN_140a4c4c0": (0, "bytes"),
}

SIZEOF = {
    "char": 1, "byte": 1, "bool": 1, "undefined1": 1,
    "short": 2, "ushort": 2, "undefined2": 2,
    "int": 4, "uint": 4, "float": 4, "undefined4": 4,
    "longlong": 8, "ulonglong": 8, "undefined8": 8, "double": 8, "long": 8,
}

CALL_RE = re.compile(r"(?:thunk_)?(FUN_[0-9a-f]{9})\s*\(")
NUM_RE = re.compile(r"^(-?\d+|0x[0-9a-fA-F]+)U?$")


def matching_close(s: str, i: int) -> int:
    """Index of the parenthesis closing the one at s[i], or -1."""
    depth = 0
    for j in range(i, len(s)):
        if s[j] == "(":
            depth += 1
        elif s[j] == ")":
            depth -= 1
            if depth == 0:
                return j
    return -1


def as_int(s: str) -> Optional[int]:
    m = NUM_RE.match(s.strip())
    return int(m.group(1), 0) if m else None


# ----------------------------------------------------------------------------------------------
# Statement tree
# ----------------------------------------------------------------------------------------------
@dataclass
class Node:
    kind: str                       # stmt | if | do | while | for | switch
    text: str = ""                  # statement text or condition text
    body: list = field(default_factory=list)
    orelse: list = field(default_factory=list)


def split_top_level(s: str, sep: str = ",") -> list[str]:
    out, depth, cur = [], 0, []
    for ch in s:
        if ch in "([{":
            depth += 1
        elif ch in ")]}":
            depth -= 1
        if ch == sep and depth == 0:
            out.append("".join(cur).strip())
            cur = []
        else:
            cur.append(ch)
    if "".join(cur).strip():
        out.append("".join(cur).strip())
    return out


class Parser:
    """Turns a Ghidra function body into a Node tree.  Tolerant of multi-line conditions."""

    def __init__(self, text: str):
        self.s = text
        self.i = 0
        self.n = len(text)

    def skip_ws(self):
        while self.i < self.n and self.s[self.i].isspace():
            self.i += 1

    def read_balanced(self, open_ch: str, close_ch: str) -> str:
        assert self.s[self.i] == open_ch, (self.s[self.i:self.i + 40])
        depth = 0
        start = self.i
        while self.i < self.n:
            ch = self.s[self.i]
            if ch == open_ch:
                depth += 1
            elif ch == close_ch:
                depth -= 1
                if depth == 0:
                    self.i += 1
                    return self.s[start + 1:self.i - 1]
            self.i += 1
        raise ValueError("unbalanced")

    def read_stmt(self) -> str:
        depth = 0
        start = self.i
        while self.i < self.n:
            ch = self.s[self.i]
            if ch in "([":
                depth += 1
            elif ch in ")]":
                depth -= 1
            elif ch == ";" and depth == 0:
                self.i += 1
                return self.s[start:self.i - 1].strip()
            elif ch == "{" and depth == 0:
                return self.s[start:self.i].strip()
            self.i += 1
        return self.s[start:].strip()

    def parse_braced(self) -> list[Node]:
        self.skip_ws()
        if self.i < self.n and self.s[self.i] == "{":
            self.expect("{")
            body = self.parse_block()
            self.expect("}")
            return body
        return [Node("stmt", self.read_stmt())]

    def parse_block(self) -> list[Node]:
        nodes = []
        while True:
            self.skip_ws()
            if self.i >= self.n:
                return nodes
            ch = self.s[self.i]
            if ch == "}":
                return nodes
            if ch == "{":
                self.i += 1
                inner = self.parse_block()
                self.expect("}")
                nodes.extend(inner)
                continue
            if self.peek_kw("if"):
                nodes.append(self.parse_if())
                continue
            if self.peek_kw("do"):
                self.i += 2
                body = self.parse_braced()
                self.skip_ws()
                assert self.peek_kw("while"), self.s[self.i:self.i + 40]
                self.i += 5
                self.skip_ws()
                cond = self.read_balanced("(", ")")
                self.skip_ws()
                if self.i < self.n and self.s[self.i] == ";":
                    self.i += 1
                nodes.append(Node("do", cond, body))
                continue
            if self.peek_kw("while"):
                self.i += 5
                self.skip_ws()
                cond = self.read_balanced("(", ")")
                body = self.parse_braced()
                nodes.append(Node("while", cond, body))
                continue
            if self.peek_kw("for"):
                self.i += 3
                self.skip_ws()
                cond = self.read_balanced("(", ")")
                body = self.parse_braced()
                nodes.append(Node("for", cond, body))
                continue
            if self.peek_kw("switch"):
                self.i += 6
                self.skip_ws()
                cond = self.read_balanced("(", ")")
                body = self.parse_braced()
                nodes.append(Node("switch", cond, body))
                continue
            stmt = self.read_stmt()
            if stmt:
                nodes.append(Node("stmt", stmt))
            elif self.i < self.n and self.s[self.i] == "{":
                self.i += 1
                inner = self.parse_block()
                self.expect("}")
                nodes.extend(inner)
        return nodes

    def parse_if(self) -> Node:
        self.i += 2
        self.skip_ws()
        cond = self.read_balanced("(", ")")
        body = self.parse_braced()
        node = Node("if", cond, body)
        self.skip_ws()
        if self.peek_kw("else"):
            self.i += 4
            self.skip_ws()
            if self.peek_kw("if"):
                node.orelse = [self.parse_if()]
            else:
                node.orelse = self.parse_braced()
        return node

    def peek_kw(self, kw: str) -> bool:
        if not self.s.startswith(kw, self.i):
            return False
        j = self.i + len(kw)
        return j >= self.n or not (self.s[j].isalnum() or self.s[j] == "_")

    def expect(self, ch: str):
        self.skip_ws()
        if self.i >= self.n or self.s[self.i] != ch:
            raise ValueError(f"expected {ch!r} at {self.s[self.i:self.i + 60]!r}")
        self.i += 1


def load_function(path: str) -> tuple[str, str, str]:
    """Returns (name, parameter list, body text)."""
    text = open(path, encoding="utf-8", errors="replace").read()
    m = re.search(r"^[\w\s\*]*?\b(FUN_[0-9a-f]{9})\s*\(([^)]*)\)\s*\n\s*\{", text, re.M)
    if not m:
        raise ValueError(f"no function header in {path}")
    name = m.group(1)
    params = m.group(2)
    start = m.end()
    depth = 1
    i = start
    while i < len(text) and depth > 0:
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
        i += 1
    return name, params, text[start:i - 1]


# ----------------------------------------------------------------------------------------------
# Read extraction
# ----------------------------------------------------------------------------------------------
@dataclass
class Read:
    kind: str                 # u8 u16 u32 u64 str varint bytes call loop if skip note return
    width: int = 0
    dest: str = ""
    callee: str = ""
    note: str = ""
    cond: str = ""
    body: list = field(default_factory=list)
    orelse: list = field(default_factory=list)
    count_from: str = ""      # variable holding the loop count
    is_count: bool = False    # this read feeds a loop count / condition
    entry_symbols: dict = field(default_factory=dict)   # loops: symbol table at loop entry


def detect_stream_param_strict(body: str) -> Optional[str]:
    """A parameter compared as the end pointer against its own cursor, with the error flag set."""
    for p in sorted(set(re.findall(r"\(\s*(param_\d)\s*\+\s*(?:0x18|6)\s*\)", body))):
        end_cmp = re.search(r"\*\(\w+\s*\*+\)\(\s*" + p + r"\s*\+\s*(?:0x18|6)\s*\)\s*<", body) or \
            re.search(r"<=?\s*\*\(\w+\s*\*+\)\(\s*" + p + r"\s*\+\s*(?:0x18|6)\s*\)", body) or \
            re.search(r"=\s*\*\(\w+\s*\*+\)\(\s*" + p + r"\s*\+\s*(?:0x18|6)\s*\)\s*;", body)
        if end_cmp and re.search(r"\(\s*" + p + r"\s*\+\s*(?:0x10|4)\s*\)", body) and \
                re.search(r"\*\(undefined1 \*\)\(\s*" + p + r"\s*\+\s*(?:0x20|8)\s*\)\s*=\s*1", body):
            return p
    return None


def detect_stream_param(body: str) -> Optional[str]:
    """A stream parameter is used as cursor (+0x10 / +4) AND as end (+0x18 / +6)."""
    strict = detect_stream_param_strict(body)
    if strict:
        return strict
    candidates = set(re.findall(r"\(\s*(param_\d)\s*\+\s*(?:0x18|6)\s*\)", body))
    for p in sorted(candidates):
        if re.search(r"\(\s*" + p + r"\s*\+\s*(?:0x10|4)\s*\)", body) and \
                re.search(r"\(\s*" + p + r"\s*\+\s*(?:0x20|8)\s*\)", body):
            return p
    for p in sorted(candidates):
        if re.search(r"\(\s*" + p + r"\s*\+\s*(?:0x10|4)\s*\)", body):
            return p
    m = re.search(r"(param_\d)\[6\]", body)
    if m:
        return m.group(1)
    return None


# Filled by the driver: every dumped function -> the parameter its own body uses as the stream
# (strict form only). Used to recognise hand-offs that Ghidra renders without the stream
# argument, e.g. `thunk_FUN_140a2ec10(param_1 + 1)` where the callee reads `param_2` (RDX passes
# through the call untouched).
STREAM_PARAM_OF_DUMP: dict[str, Optional[str]] = {}


class FunctionAnalysis:
    def __init__(self, name: str, params: str, body: str, stream_hint: Optional[str] = None):
        self.name = name
        self.params = [p.strip() for p in params.split(",") if p.strip()]
        self.body = body
        detected = detect_stream_param(body)
        self.warnings: list[str] = []
        if stream_hint and detected and stream_hint != detected:
            self.warnings.append(f"caller passes the stream as {stream_hint} but inline reads use {detected}; "
                                 f"using the caller's {stream_hint}")
        self.stream = stream_hint or detected
        self.stream_from_hint = detected is None and stream_hint is not None
        self.symbols: dict[str, str] = {}
        self.history: dict[str, list[str]] = {}
        self.reads: list[Read] = []
        self.callees: dict[str, int] = {}

    def define(self, name: str, value: str):
        self.symbols[name] = value
        self.history.setdefault(name, []).append(value)

    # -- expression helpers -----------------------------------------------------------------
    def resolve(self, expr: str, depth: int = 0) -> str:
        expr = expr.strip()
        if depth > 4:
            return expr
        m = re.fullmatch(r"\(?\s*(\w+)\s*\)?", expr)
        if m and m.group(1) in self.symbols and self.symbols[m.group(1)] != "<READ>":
            return self.resolve(self.symbols[m.group(1)], depth + 1)
        return expr

    def is_end(self, expr: str) -> bool:
        e = self.resolve(expr)
        e = re.sub(r"^\(\w+\s*\*+\)", "", e).strip()
        return bool(re.fullmatch(rf"\*\(\w+\s*\*+\)\(\s*{self.stream}\s*\+\s*(0x18|6)\s*\)", e))

    def cursor_plus(self, expr: str, depth: int = 0) -> Optional[int]:
        """If expr denotes `cursor + k` (typed), return the byte width k*sizeof(T)."""
        if depth > 6:
            return None
        e = self.resolve(expr).strip()
        m = re.fullmatch(r"\(\w+\s*\*+\)\((.*)\)", e)
        if m:
            e = m.group(1).strip()
        m = re.fullmatch(r"\*\((\w+)\s*(\*+)\)\(\s*" + self.stream +
                         r"\s*\+\s*(0x10|4)\s*\)\s*\+\s*(\d+|0x[0-9a-fA-F]+)U?", e)
        if m:
            ty, stars, k = m.group(1), m.group(2), int(m.group(4), 0)
            if len(stars) >= 2:
                return k * SIZEOF.get(ty, 0)
            return k
        m = re.fullmatch(r"(\w+)\s*\+\s*(\d+)", e)
        if m and m.group(1) in self.symbols:
            name = m.group(1)
            base = self.symbols[name]
            if re.fullmatch(name + r"\s*\+\s*\d+", base):
                # `p = p + 1` inside a loop: the element width comes from the earlier definition
                for earlier in reversed(self.history.get(name, [])):
                    if not re.fullmatch(name + r"\s*\+\s*\d+", earlier) and earlier != "<READ>":
                        base = earlier
                        break
            if base == "<READ>":
                return None
            w = self.cursor_plus(base, depth + 1)
            if w is not None:
                return w
        return None

    def read_from_cond(self, cond: str) -> Optional[tuple[int, bool]]:
        """Returns (width, then_is_success) for a bounds-check condition, else None."""
        if "<=" in cond:
            parts = split_top_level(cond, "<")
            if len(parts) == 2 and parts[1].startswith("="):
                left, right = parts[0], parts[1][1:]
                if self.is_end(right):
                    w = self.cursor_plus(left)
                    if w is not None:
                        return w, True
            return None
        parts = split_top_level(cond, "<")
        if len(parts) != 2:
            return None
        left, right = parts
        if self.is_end(left):
            w = self.cursor_plus(right)
            if w is not None:
                return w, False
        return None

    def dest_of(self, nodes: list[Node]) -> str:
        for n in nodes:
            if n.kind == "stmt":
                m = re.match(r"(.+?)\s*=\s*(?:\([\w ]+\))?\*\*\(", n.text)
                if m:
                    return m.group(1).strip()
                m = re.match(r"(\w+)\s*=\s*CONCAT44\(.*\*\*\(", n.text)
                if m:
                    return m.group(1).strip()
        return ""

    @staticmethod
    def has_return(nodes: list[Node]) -> bool:
        return any(n.kind == "stmt" and n.text.startswith("return") for n in nodes)

    # -- walk ----------------------------------------------------------------------------------
    def walk(self, nodes: list[Node]) -> list[Read]:
        out: list[Read] = []
        for idx, n in enumerate(nodes):
            if n.kind == "stmt":
                self.stmt(n.text, out)
            elif n.kind == "if":
                rc = self.read_from_cond(n.text)
                if rc is not None:
                    w, then_success = rc
                    kind = {1: "u8", 2: "u16", 4: "u32", 8: "u64"}.get(w, f"u{w * 8}")
                    if then_success:
                        # `if (cursor+k <= end) { value = *cursor; ...more... }` [else fail]
                        out.append(Read(kind, w, dest=self.dest_of(n.body)))
                        out.extend(self.walk(n.body))
                    elif n.orelse:
                        # `if (end < cursor+k) { fail } else { value = *cursor; ...more... }`
                        out.append(Read(kind, w, dest=self.dest_of(n.orelse)))
                        out.extend(self.walk(n.orelse))
                    elif self.has_return(n.body):
                        # `if (end < cursor+k) { fail; return; }  value = *cursor; ...`
                        out.append(Read(kind, w, dest=self.dest_of(nodes[idx + 1:idx + 3])))
                    else:
                        out.append(Read("skip", w, note="failure-branch-only read (pointer advanced by loop)"))
                else:
                    cond_r = Read("if", cond=n.text, entry_symbols=dict(self.symbols))
                    cond_r.body = self.walk(n.body)
                    cond_r.orelse = self.walk(n.orelse)
                    if cond_r.body or cond_r.orelse:
                        out.append(cond_r)
            elif n.kind in ("do", "while", "for"):
                loop = Read("loop", cond=n.text, note=n.kind, entry_symbols=dict(self.symbols))
                loop.body = self.walk(n.body)
                if loop.body:
                    out.append(loop)
            elif n.kind == "switch":
                sw = Read("if", cond="switch " + n.text, note="switch")
                sw.body = self.walk(n.body)
                if sw.body:
                    out.append(sw)
        return out

    def stmt(self, text: str, out: list[Read]):
        if text.startswith("return"):
            out.append(Read("return"))
            return
        m = re.match(r"^(\w+)\s*=\s*(.+)$", text, re.S)
        if m and "(" not in m.group(1):
            rhs = m.group(2).strip()
            src = re.fullmatch(r"(?:\(\w+\))?(\w+)", rhs)
            if src and self.symbols.get(src.group(1)) == "<READ>":
                self.define(m.group(1), "<READ>")
            elif "**(" in rhs and self.stream and self.stream in rhs:
                self.define(m.group(1), "<READ>")
            else:
                self.define(m.group(1), rhs)
        for cm in CALL_RE.finditer(text):
            callee = cm.group(1)
            start = cm.end() - 1
            depth = 0
            j = start
            while j < len(text):
                if text[j] == "(":
                    depth += 1
                elif text[j] == ")":
                    depth -= 1
                    if depth == 0:
                        break
                j += 1
            args = split_top_level(text[start + 1:j])
            stream_pos = None
            for k, a in enumerate(args):
                a2 = re.sub(r"^\(\w+\s*\*?\)", "", a).strip()
                if a2 == self.stream:
                    stream_pos = k
            hidden = False
            if stream_pos is None:
                # Ghidra may omit an argument the callee still consumes; the callee's own body
                # tells us which parameter (register) it reads the stream from.
                callee_stream = STREAM_PARAM_OF_DUMP.get(callee)
                if callee_stream and self.stream:
                    p = int(callee_stream[-1]) - 1
                    if p >= len(args):
                        if callee_stream == self.stream:
                            stream_pos = p
                            hidden = True
                        else:
                            out.append(Read("note", note=f"POSSIBLE hidden hand-off to {callee}: callee reads "
                                                         f"{callee_stream}, caller's stream is {self.stream}"))
            if stream_pos is None:
                continue
            self.callees[callee] = stream_pos
            if hidden:
                out.append(Read("call", callee=callee, note=f"stream at arg {stream_pos} (hidden: register pass-through)"))
                continue
            if callee in PRIMITIVES:
                pidx, kind = PRIMITIVES[callee]
                dest = args[1 - pidx] if len(args) > 1 else ""
                out.append(Read(kind, dest=dest, callee=callee))
            else:
                out.append(Read("call", callee=callee, note=f"stream at arg {stream_pos}"))
        if "(code" in text and self.stream and re.search(rf"\b{self.stream}\b", text):
            out.append(Read("note", note="VIRTUAL CALL with stream: " + " ".join(text.split())[:140]))
        if "goto" in text or re.match(r"^LAB_", text):
            out.append(Read("note", note="LABEL/GOTO: " + " ".join(text.split())[:80]))


# ----------------------------------------------------------------------------------------------
# Loop / count association and minimal-record evaluation
# ----------------------------------------------------------------------------------------------
def annotate_counts(reads: list[Read]):
    """Mark reads whose value guards a following `if (0 < (int)var)` block."""
    for idx, r in enumerate(reads):
        if r.kind == "if":
            m = re.fullmatch(r"\s*0\s*<\s*\(int\)(\w+)\s*", r.cond) or re.fullmatch(r"\s*0\s*<\s*(\w+)\s*", r.cond)
            if m:
                r.count_from = m.group(1)
                for back in range(idx - 1, -1, -1):
                    if reads[back].kind in ("u32", "u8", "u16", "u64", "varint"):
                        reads[back].is_count = True
                        reads[back].note = (reads[back].note + " count for following block").strip()
                        break
            annotate_counts(r.body)
            annotate_counts(r.orelse)
        elif r.kind == "loop":
            annotate_counts(r.body)


class StopFunction(Exception):
    pass


class Evaluator:
    """Walks the read tree assuming every stream value is zero; emits the minimal byte layout."""

    def __init__(self, analyses: dict[str, "FunctionAnalysis"]):
        self.analyses = analyses
        self.rows: list[dict] = []
        self.offset = 0
        self.review: list[str] = []
        self.stack: list[str] = []
        self.symbols_at: Optional[dict] = None

    def emit(self, fn: str, r: Read, width: int, value_note: str = "0"):
        self.rows.append({
            "offset": self.offset, "width": width, "kind": r.kind, "dest": r.dest,
            "func": fn, "note": r.note, "value": value_note,
        })
        self.offset += width

    def is_read(self, var: str, fa: FunctionAnalysis) -> bool:
        table = self.symbols_at if self.symbols_at is not None else fa.symbols
        return table.get(var) == "<READ>"

    def eval_cond(self, cond: str, fa: FunctionAnalysis, symbols_at: Optional[dict] = None) -> Optional[bool]:
        self.symbols_at = symbols_at
        c = " ".join(cond.split()).strip()
        while c.startswith("(") and c.endswith(")") and matching_close(c, 0) == len(c) - 1:
            c = c[1:-1].strip()
        m = re.fullmatch(r"0 < (?:\((?:int|uint|longlong)\))?(\w+)", c)
        if m:
            return False if self.is_read(m.group(1), fa) else None
        m = re.fullmatch(r"\(?(?:\((?:char|int|uint|bool)\))?(\w+)\)? != (?:0|'\\0')", c)
        if m:
            return False if self.is_read(m.group(1), fa) else None
        m = re.fullmatch(r"\(?(?:\((?:char|int|uint|bool)\))?(\w+)\)? == (?:0|'\\0')", c)
        if m:
            return True if self.is_read(m.group(1), fa) else None
        m = re.fullmatch(r"\(?(?:\((?:int|uint)\))?(\w+)\)? < (\d+)", c)
        if m and int(m.group(2)) > 0:
            return True if self.is_read(m.group(1), fa) else None
        if m and int(m.group(2)) == 0:
            return False if self.is_read(m.group(1), fa) else None
        m = re.fullmatch(r"\(int\)(\w+) <= \(int\)\w+ - \*\(int \*\)\(" + (fa.stream or "param_9") + r" \+ (?:0x10|4)\)", c)
        if m:
            return True if self.is_read(m.group(1), fa) else None
        if fa.stream and re.fullmatch(r"\(char\)" + fa.stream + r"\[8\] != '\\0'", c):
            return False
        if fa.stream and re.fullmatch(r"\*\(char \*\)\(" + fa.stream + r" \+ 0x20\) != '\\0'", c):
            return False
        if fa.stream and re.fullmatch(r"\*\(char \*\)\(" + fa.stream + r" \+ 0x20\) == '\\0'", c):
            return True
        return None

    def fixed_loop_count(self, r: Read) -> Optional[int]:
        """`lVar = N; do { ...; lVar = lVar + -1; } while (lVar != 0)` -> N."""
        m = re.fullmatch(r"\s*(\w+)\s*!=\s*0\s*", r.cond)
        if not m:
            return None
        var = m.group(1)
        n = as_int(r.entry_symbols.get(var, ""))
        if n is None or n <= 0 or n > 64:
            return None
        return n

    def walk(self, fn: str, reads: list[Read], depth: int = 0):
        fa = self.analyses.get(fn)
        for r in reads:
            if r.kind in ("u8", "u16", "u32", "u64", "skip"):
                self.emit(fn, r, r.width)
            elif r.kind == "str":
                self.emit(fn, r, 4, value_note="i32 0 (empty string)")
            elif r.kind == "bytes":
                self.emit(fn, r, 4, value_note="u32 0 (empty bytes)")
            elif r.kind == "varint":
                self.emit(fn, r, 1, value_note="varint 0 (one byte 0x00)")
            elif r.kind == "return":
                raise StopFunction()
            elif r.kind == "call":
                if r.callee in self.analyses and self.analyses[r.callee].stream:
                    if r.callee in self.stack:
                        self.review.append(f"{fn}: recursive call into {r.callee} skipped")
                        continue
                    self.stack.append(r.callee)
                    self.rows.append({"offset": self.offset, "width": 0, "kind": "enter", "dest": "",
                                      "func": r.callee, "note": f"called from {fn}", "value": ""})
                    try:
                        self.walk(r.callee, self.analyses[r.callee].reads, depth + 1)
                    except StopFunction:
                        pass
                    self.rows.append({"offset": self.offset, "width": 0, "kind": "leave", "dest": "",
                                      "func": r.callee, "note": "", "value": ""})
                    self.stack.pop()
                elif r.callee in self.analyses:
                    self.review.append(f"{fn} @ blob {self.offset}: callee {r.callee} takes the stream but "
                                       f"no stream use was detected inside it ({r.note}) - check by hand")
                else:
                    self.review.append(f"{fn} @ blob {self.offset}: callee {r.callee} has no dump ({r.note})")
                    self.rows.append({"offset": self.offset, "width": 0, "kind": "MISSING", "dest": "",
                                      "func": r.callee, "note": f"no dump; called from {fn}", "value": ""})
            elif r.kind == "if":
                v = self.eval_cond(r.cond, fa, r.entry_symbols or None) if fa else None
                if v is True:
                    self.walk(fn, r.body, depth)
                elif v is False:
                    self.walk(fn, r.orelse, depth)
                else:
                    has_body = bool(r.body)
                    has_else = bool(r.orelse)
                    cond = " ".join(r.cond.split())[:110]
                    if has_body and not has_else:
                        self.review.append(f"{fn} @ blob {self.offset}: condition `{cond}` undecided; "
                                           f"only the then-branch reads ({len(r.body)} item(s)); assumed NOT taken")
                    elif has_else and not has_body:
                        self.review.append(f"{fn} @ blob {self.offset}: condition `{cond}` undecided; "
                                           f"only the else-branch reads; assumed taken (else skipped)")
                    else:
                        self.review.append(f"{fn} @ blob {self.offset}: condition `{cond}` undecided; "
                                           f"BOTH branches read - manual decision required (then assumed)")
                        self.walk(fn, r.body, depth)
            elif r.kind == "loop":
                n = self.fixed_loop_count(r)
                if n is not None:
                    for _ in range(n):
                        self.walk(fn, r.body, depth)
                    continue
                m = re.fullmatch(r"\s*\(?\w+\)?\s*<\s*\(?(?:\(int\))?(\w+)\)?\s*", r.cond)
                self.symbols_at = r.entry_symbols or None
                if r.note == "while" and m and fa and self.is_read(m.group(1), fa):
                    continue  # while-loop with zero trip count
                self.review.append(f"{fn} @ blob {self.offset}: loop `{r.note} ({' '.join(r.cond.split())[:80]})` "
                                   f"with {len(r.body)} read item(s) reached on the minimal path - review")
            elif r.kind == "note":
                self.review.append(f"{fn} @ blob {self.offset}: {r.note}")


# ----------------------------------------------------------------------------------------------
# Self-check: every successful inline read advances the cursor exactly once
# ----------------------------------------------------------------------------------------------
def count_raw_advances(fa: FunctionAnalysis) -> int:
    """Every assignment to the cursor that is not a pin-to-end is one successful read."""
    S = fa.stream
    body = fa.body
    cur = r"\(\s*" + S + r"\s*\+\s*(?:0x10|4)\s*\)"
    end = r"\(\s*" + S + r"\s*\+\s*(?:0x18|6)\s*\)"
    n = 0
    for m in re.finditer(r"\*\(\w+\s*\*+\)" + cur + r"\s*=\s*([^;]+);", body):
        rhs = m.group(1).strip()
        if re.search(end, rhs):
            continue                      # cursor = end (failure path)
        t = re.fullmatch(r"\(?\s*(\w+)\s*\)?", rhs)
        if t:
            tmp = t.group(1)
            first = re.search(r"\b" + tmp + r"\s*=\s*([^;]+);", body)
            if first and re.search(end, first.group(1)):
                continue                  # temp that holds the end pointer
        n += 1
    return n


def count_extracted_reads(reads: list[Read]) -> int:
    n = 0
    for r in reads:
        if r.kind in ("u8", "u16", "u32", "u64", "skip"):
            n += 1
        elif r.kind in ("if", "loop"):
            n += count_extracted_reads(r.body) + count_extracted_reads(r.orelse)
    return n


def count_raw_stream_uses(fa: FunctionAnalysis) -> int:
    """Uses of the stream parameter other than its own field accesses - i.e. hand-offs to callees."""
    S = fa.stream
    body = fa.body
    total = len(re.findall(r"\b" + S + r"\b", body))
    fields = len(re.findall(r"\(\s*" + S + r"\s*\+\s*(?:0x10|0x18|0x20|4|6|8)\s*\)", body))
    indexed = len(re.findall(r"\b" + S + r"\[\d+\]", body))
    base = len(re.findall(r"\*" + S + r"\b", body))
    return total - fields - indexed - base


def count_extracted_uses(reads: list[Read]) -> int:
    n = 0
    for r in reads:
        if r.kind == "call" and "hidden" in r.note:
            continue                      # not visible in the C text by definition
        if r.kind in ("call", "str", "varint", "bytes"):
            n += 1
        elif r.kind == "note" and r.note.startswith("VIRTUAL CALL"):
            n += 1                        # polymorphic element loader; the evaluator reports it if reached
        elif r.kind in ("if", "loop"):
            n += count_extracted_uses(r.body) + count_extracted_uses(r.orelse)
    return n


# ----------------------------------------------------------------------------------------------
# Driver
# ----------------------------------------------------------------------------------------------
def find_dumps(dirs: list[str]) -> dict[str, str]:
    dumps: dict[str, str] = {}
    for d in dirs:
        for root, _, files in os.walk(d):
            for f in files:
                m = re.match(r"(FUN_[0-9a-f]{9})_[0-9a-f]{9}\.c$", f)
                if m and m.group(1) not in dumps:
                    dumps[m.group(1)] = os.path.join(root, f)
    return dumps


def analyse(name: str, dumps: dict, analyses: dict, todo: list, stream_arg: Optional[int]):
    if name in analyses or name not in dumps:
        return
    fname, params, body = load_function(dumps[name])
    hint = f"param_{stream_arg + 1}" if stream_arg is not None else None
    fa = FunctionAnalysis(fname, params, body, hint)
    analyses[name] = fa
    if fa.stream is None:
        fa.warnings.append("no stream parameter detected (not a loader?)")
        return
    if fa.stream_from_hint:
        fa.warnings.append(f"stream parameter {fa.stream} taken from the caller (no inline read here)")
    try:
        tree = Parser(body).parse_block()
    except Exception as ex:  # noqa: BLE001
        fa.warnings.append(f"parse failure: {ex}")
        return
    fa.reads = fa.walk(tree)
    annotate_counts(fa.reads)
    for c, pos in fa.callees.items():
        if c not in PRIMITIVES:
            todo.append((c, pos))
    # A call into a function that has no dump cannot be checked for a hidden (register) hand-off
    # of the stream: FUN_140a30370's 16 missing bytes hid in exactly such a call. List them.
    for cm in CALL_RE.finditer(body):
        callee = cm.group(1)
        if callee not in dumps and callee not in PRIMITIVES and callee not in fa.callees:
            fa.warnings.append(f"calls {callee}, which has no dump: cannot rule out a hidden stream hand-off")


def fmt_reads(reads: list[Read], indent: int = 0) -> list[str]:
    lines = []
    pad = "  " * indent
    for r in reads:
        if r.kind in ("u8", "u16", "u32", "u64", "skip"):
            extra = " [COUNT]" if r.is_count else ""
            note = (" ; " + r.note) if r.note and not r.is_count else ""
            lines.append(f"{pad}{r.kind:<6} -> {r.dest or '(local)'}{extra}{note}")
        elif r.kind in ("str", "varint", "bytes"):
            lines.append(f"{pad}{r.kind:<6} -> {r.dest or '(local)'}   ({r.callee})")
        elif r.kind == "call":
            lines.append(f"{pad}call   {r.callee}   ({r.note})")
        elif r.kind == "return":
            lines.append(f"{pad}return")
        elif r.kind == "if":
            tag = f"   [count var {r.count_from}]" if r.count_from else ""
            lines.append(f"{pad}if ({' '.join(r.cond.split())[:110]}){tag}")
            lines.extend(fmt_reads(r.body, indent + 1))
            if r.orelse:
                lines.append(f"{pad}else")
                lines.extend(fmt_reads(r.orelse, indent + 1))
        elif r.kind == "loop":
            lines.append(f"{pad}loop {r.note} ({' '.join(r.cond.split())[:80]})")
            lines.extend(fmt_reads(r.body, indent + 1))
        elif r.kind == "note":
            lines.append(f"{pad}NOTE {r.note}")
    return lines


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dumpdirs", nargs="+")
    ap.add_argument("--root", required=True)
    ap.add_argument("--md")
    ap.add_argument("--json")
    args = ap.parse_args()

    dumps = find_dumps(args.dumpdirs)
    bodies: dict[str, str] = {}
    for fn, path in dumps.items():
        try:
            bodies[fn] = load_function(path)[2]
            STREAM_PARAM_OF_DUMP[fn] = detect_stream_param_strict(bodies[fn])
        except ValueError:
            STREAM_PARAM_OF_DUMP[fn] = None
    # Transitive closure: a function that only forwards a parameter to a known stream reader (at
    # that reader's stream position) uses that parameter as its stream too.
    changed = True
    while changed:
        changed = False
        for fn, body in bodies.items():
            if STREAM_PARAM_OF_DUMP.get(fn):
                continue
            for cm in CALL_RE.finditer(body):
                callee = cm.group(1)
                cs = STREAM_PARAM_OF_DUMP.get(callee) or PRIMITIVES.get(callee, (None,))[0]
                if cs is None:
                    continue
                pos = int(cs[-1]) - 1 if isinstance(cs, str) else cs
                start = cm.end() - 1
                depth, j = 0, start
                while j < len(body):
                    if body[j] == "(":
                        depth += 1
                    elif body[j] == ")":
                        depth -= 1
                        if depth == 0:
                            break
                    j += 1
                cargs = split_top_level(body[start + 1:j])
                if pos < len(cargs):
                    a = re.sub(r"^\(\w+\s*\*?\)", "", cargs[pos]).strip()
                    if re.fullmatch(r"param_\d", a):
                        STREAM_PARAM_OF_DUMP[fn] = a
                        changed = True
                        break
    analyses: dict[str, FunctionAnalysis] = {}
    todo: list[tuple[str, Optional[int]]] = [(args.root, None)]
    order = []
    while todo:
        name, pos = todo.pop(0)
        if name in analyses:
            continue
        analyse(name, dumps, analyses, todo, pos)
        if name in analyses:
            order.append(name)

    ev = Evaluator(analyses)
    ev.stack.append(args.root)
    try:
        ev.walk(args.root, analyses[args.root].reads)
    except StopFunction:
        pass

    out = []
    out.append(f"# Self-record read sequence extracted from {args.root}\n")
    out.append(f"Dumps: {', '.join(args.dumpdirs)}  \nFunctions analysed: {len(order)}  \n"
               f"Minimal record length: **{ev.offset} bytes**\n")
    missing = sorted({r['func'] for r in ev.rows if r['kind'] == 'MISSING'})
    if missing:
        out.append("\n## Missing callee dumps (stream is passed to them)\n")
        out.extend(f"- `{m}`" for m in missing)
    out.append("\n## Review items on the minimal path\n")
    if ev.review:
        out.extend(f"- {x}" for x in ev.review)
    else:
        out.append("- none")
    out.append("\n## Self-check: raw cursor advances vs extracted inline reads\n")
    mismatches = []
    for name in order:
        fa = analyses[name]
        if not fa.stream:
            continue
        raw = count_raw_advances(fa)
        got = count_extracted_reads(fa.reads)
        if raw != got:
            mismatches.append(f"- MISMATCH {name}: {raw} cursor advance(s) in the C, {got} inline read(s) extracted")
        raw_uses = count_raw_stream_uses(fa)
        got_uses = count_extracted_uses(fa.reads)
        if raw_uses != got_uses:
            mismatches.append(f"- MISMATCH {name}: the stream is handed to callees {raw_uses} time(s) in the C, "
                              f"{got_uses} call(s)/helper read(s) extracted")
    out.extend(mismatches if mismatches else ["- every analysed function: advances == extracted reads"])
    out.append("\n## Warnings\n")
    warned = False
    for name in order:
        for w in analyses[name].warnings:
            out.append(f"- {name}: {w}")
            warned = True
    if not warned:
        out.append("- none")
    out.append("\n## Minimal record byte map (all stream values zero)\n")
    out.append("| blob off | width | kind | dest | function | note |\n|---|---|---|---|---|---|")
    for r in ev.rows:
        if r["kind"] in ("enter", "leave"):
            out.append(f"| {r['offset']} | | **{r['kind']} {r['func']}** | | | {r['note']} |")
        else:
            out.append(f"| {r['offset']} | {r['width']} | {r['kind']} | `{r['dest']}` | {r['func']} | {r['note']} {r['value']} |")
    out.append("\n## Per-function read trees\n")
    for name in order:
        fa = analyses[name]
        out.append(f"\n### {name}  (stream = {fa.stream}, params: {', '.join(fa.params)})\n")
        for w in fa.warnings:
            out.append(f"- WARNING: {w}")
        out.append("```")
        out.extend(fmt_reads(fa.reads))
        out.append("```")
    # Two distinct claims, so a caller is never misled:
    #  * length_authoritative — nothing was left unresolved *on the all-zero minimal path*, so the
    #    emitted length is trustworthy. Only review items and MISSING rows are on that path; the
    #    global self-check and hidden-hand-off warnings live in list-element loaders the minimal
    #    path never enters.
    #  * fully_resolved — additionally, every dumped function was understood completely (no
    #    self-check mismatch, no callee that takes the stream but has no dump). Required before
    #    the full grammar (non-empty lists) can be trusted, not just the minimal length.
    warnings_by_func = {name: analyses[name].warnings for name in order if analyses[name].warnings}
    blocking_warnings = [
        f"{name}: {w}"
        for name, ws in warnings_by_func.items()
        for w in ws
        if "no dump" in w or "parse failure" in w
    ]
    length_authoritative = not (ev.review or missing)
    fully_resolved = length_authoritative and not (mismatches or blocking_warnings)

    text = "\n".join(out)
    if args.md:
        open(args.md, "w", encoding="utf-8").write(text)
    else:
        print(text)
    if args.json:
        json.dump({"root": args.root, "length": ev.offset,
                   "length_authoritative": length_authoritative, "fully_resolved": fully_resolved,
                   "rows": ev.rows, "review": ev.review, "order": order, "mismatches": mismatches,
                   "missing": missing, "blocking_warnings": blocking_warnings,
                   "warnings": warnings_by_func}, open(args.json, "w"), indent=1)
    status = ("length AUTHORITATIVE" if length_authoritative else "length NOT trustworthy") \
        + (", fully resolved" if fully_resolved else ", grammar INCOMPLETE (element loaders)")
    print(f"[selfschema] {len(order)} functions, minimal record {ev.offset} bytes [{status}], "
          f"{len(ev.review)} review items, {len(missing)} missing dumps, {len(mismatches)} self-check mismatches, "
          f"{len(blocking_warnings)} blocking warning(s)", file=sys.stderr)
    if not fully_resolved:
        for item in ev.review + [f"MISSING dump: {m}" for m in missing] + mismatches + blocking_warnings:
            print(f"  - {item}", file=sys.stderr)
        # Nonzero exit whenever anything is unresolved, so a caller never treats an incomplete
        # derivation as success — even when only skipped element loaders remain.
        sys.exit(1 if length_authoritative else 2)


if __name__ == "__main__":
    main()
