"""Memory-only adapter for the reviewed 1315 reassembly. No replay or payload export."""
from __future__ import annotations

import ast
import contextlib
import hashlib
import io
import pathlib
import sys
import types
import struct
import zlib

sys.dont_write_bytecode = True
WORKSPACE = pathlib.Path(__file__).resolve().parents[3]
REFERENCE = pathlib.Path("C:/Aug2017/captures/reference/z1br-live-20260912T062647636Z-327e9258")
TEAM = pathlib.Path("C:/Aug2017/parallel/z1br-parity-20260912")
REPORTS = TEAM / "reports/evidence"
CAPTURE_SHA = "bb8e7da0df469f17e0d24fa3366452cf83068c9eef6971f5f58fcae2cd54ade8"


def load_capture(reference=REFERENCE):
    """Reuse the established audit, removing its single historical report write.

    The original module/file is never edited. Its transport code, CRC checks,
    session chaining and in-memory return values remain unchanged. Refuse any
    unexpected write call before executing it. Imported decoders are never run.
    """
    path = reference / "review/review-network-sanitized.py"
    source = path.read_text(encoding="utf-8-sig")
    tree = ast.parse(source, filename=str(path))
    writes = [n for n in ast.walk(tree) if isinstance(n, ast.Call)
              and isinstance(n.func, ast.Attribute)
              and n.func.attr in ("write_text", "write_bytes")]
    if len(writes) != 1 or "review/network-sanitized.json" not in ast.unparse(writes[0]):
        raise ValueError("reviewed audit write contract changed")
    write_call = writes[0]

    class SuppressReport(ast.NodeTransformer):
        def visit_Expr(self, node):
            if node.value is write_call:
                return ast.copy_location(ast.Pass(), node)
            return self.generic_visit(node)

    tree = ast.fix_missing_locations(SuppressReport().visit(tree))
    audit = types.ModuleType("parity_review_memory")
    audit.__file__ = str(path)
    exec(compile(tree, str(path), "exec"), audit.__dict__)
    audit.ROOT = reference
    audit.SERVER = WORKSPACE
    audit.SOE = audit.module("parity_soe", WORKSPACE / "tools/diagnostics/decode_z1br_vehicles.py")
    audit.TABLE = audit.module("parity_august_table", WORKSPACE / "tools/weapons/decode-friend-table.py")
    with contextlib.redirect_stdout(io.StringIO()):
        report, decoded = audit.run()
    if report["captureSha256"] != CAPTURE_SHA:
        raise ValueError("capture hash changed")
    if report["truncatedFrames"] or any(s.get("crcFailureFrames") for s in report["streams"]):
        raise ValueError("capture integrity failure")
    # The historical SOE walker keeps raw channel-2 datagrams intact. These
    # carry the same negotiated CRC trailer; reliable decoded messages do not.
    sessions, _ = audit.load()
    seeds = {}
    for ordinal, (_, rows) in enumerate(sorted(sessions.items(), key=lambda p: p[1][0][0]), 1):
        reply = next((r[3] for r in rows if r[3][:2] == b"\0\2"), None)
        if reply is not None:
            seeds[ordinal] = zlib.crc32(reply[6:10][::-1])
    normalized = []
    raw_crc_frames = []
    for stream, frame, time, direction, data, reliable in decoded:
        if data[:1] in (b"\x45", b"\x46") and not reliable:
            if len(data) < 3 or (zlib.crc32(data[:-2], seeds[stream]) & 65535) != int.from_bytes(data[-2:], "big"):
                raise ValueError(f"raw movement CRC mismatch at frame {frame}")
            data = data[:-2]
            raw_crc_frames.append(frame)
        normalized.append((stream, frame, time, direction, data, reliable))
    report["additionalRawMovementCrcFrames"] = raw_crc_frames
    return audit, report, normalized


def module(name, path):
    import importlib.util
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec)
    sys.modules[name] = result
    spec.loader.exec_module(result)
    return result


def sha(data):
    return hashlib.sha256(data).hexdigest()
