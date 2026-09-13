#!/usr/bin/env python3
"""packread - Cranberry's reader for the August-2017 H1Z1 client's ``.pack`` archives.

INPUT
    ``C:\\Aug2017\\Client\\Resources\\Assets\\Assets_000..255.pack`` - 256 Forgelight v1
    ``.pack`` containers, 14,091,393,113 bytes (13.1 GiB) total, holding 50,502 assets
    for client build 0.0.118.208059.

OUTPUT
    ``index``   an asset index as TSV (default) or JSON: name, pack, offset, size, crc32.
    ``find``    matching assets printed as ``size  pack  offset  name``.
    ``extract`` the named assets written into a directory, one file per asset.
    ``cat``     one asset's bytes on stdout (or to ``-o FILE``).

    Nothing is ever written back into the client. Every pack is opened read-only.

FORMAT FACTS THIS TOOL RELIES ON
    All of the following were verified directly against
    ``C:\\Aug2017\\Client\\Resources\\Assets\\Assets_*.pack`` on 2026-08-29; see
    ``docs/27-client-pack-format.md`` for the byte tables and the evidence for each field.
    The container is the public Forgelight v1 ``.pack`` layout (protocol-level knowledge,
    clean-room rule 5); the numbers below are this project's own measurements.

    F1  Every integer is BIG-endian, unsigned, 4 bytes. (``Assets_000.pack`` +0 reads
        0x04662EBD big-endian = 73,805,501, in range for the 131,941,265-byte file; read
        little-endian the same bytes are 0xBD2E6604 = 3,173,934,596, past the end.)
    F2  A pack is a singly linked list of chunks. Chunk 0 starts at file offset 0. Each
        chunk header is ``u32 nextChunkOffset`` (absolute; 0 ends the chain) then
        ``u32 fileCount`` (entries in THIS chunk only). 256 packs hold 470 chunks; the
        chunk-0 ``fileCount`` values sum to 36,097 of the 50,502 entries, so reading only
        chunk 0 would lose 14,405 assets - over a quarter of the corpus (28.5%).
    F3  Each entry is ``u32 nameLength``, ``nameLength`` bytes of ASCII name (NOT
        nul-terminated), ``u32 dataOffset`` (absolute, same file), ``u32 dataSize``,
        ``u32 crc32``.
    F4  ``crc32`` is CRC-32/ISO-HDLC (zlib ``crc32``) over the ``dataSize`` bytes exactly as
        they are stored. Checked against every entry this tool extracts.
    F5  Names are printable ASCII with no path separators; the longest is 72 bytes. Names
        are unique across the whole corpus (50,502 entries, 50,502 distinct names), so a
        name identifies exactly one asset in this client.
    F6  Nothing in this client is compressed. All 50,502 assets are stored raw: none carries
        the big-endian 0xA1B2C3D4 + inflated-size + zlib preamble that other Forgelight
        containers use, none has ``dataSize == 0``, and no ``dataOffset + dataSize`` runs
        past its file. The preamble is still *detected* per asset (never assumed) so a pack
        from another build does not decode to garbage silently.

USAGE
    python packread.py index   --assets <dir> -o out\\pack-index.tsv
    python packread.py find    "*.zone"
    python packread.py extract "Models.txt" "*.zone" -o C:\\Aug2017\\out\\world_aug
    python packread.py cat     "Z2Areas.xml" -o Z2Areas.xml

Run ``python packread.py <subcommand> --help`` for per-subcommand options.
"""

from __future__ import annotations

import argparse
import fnmatch
import json
import os
import struct
import sys
import zlib
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Iterator, Sequence

# --------------------------------------------------------------------------------------
# Format constants (see F1-F6 in the module docstring)
# --------------------------------------------------------------------------------------

#: Default location of the August client's asset packs.
DEFAULT_ASSETS_DIR = Path(r"C:\Aug2017\Client\Resources\Assets")

#: Glob for the pack files inside that directory.
PACK_GLOB = "Assets_*.pack"

#: F1: every scalar in the container is a big-endian u32.
_U32 = struct.Struct(">I")
#: Chunk header: nextChunkOffset, fileCount (F2).
_CHUNK_HEADER = struct.Struct(">II")
#: Entry tail after the name: dataOffset, dataSize, crc32 (F3).
_ENTRY_TAIL = struct.Struct(">III")

#: F6: big-endian preamble that marks a zlib-wrapped asset in other Forgelight builds.
#: Never observed in this client; detected anyway so a foreign pack cannot decode silently.
COMPRESSED_MAGIC = 0xA1B2C3D4

#: F5: longest observed name is 72 bytes. A generous ceiling that still catches a desync
#: within one entry rather than allocating from a misread length field.
MAX_NAME_LENGTH = 512

#: Smallest structurally possible pack: one chunk header with zero entries.
MIN_PACK_LENGTH = _CHUNK_HEADER.size

#: Copy granularity for streaming extraction. Assets run to 45 MB (``Z2.zone``), so nothing
#: is ever read whole into memory unless the caller asks for bytes.
COPY_BLOCK = 1 << 20


class PackFormatError(Exception):
    """Raised when a pack does not match the verified v1 layout."""


@dataclass(frozen=True, slots=True)
class Entry:
    """One asset located inside one pack. Holds no data - only where the data lives."""

    name: str
    pack: Path
    offset: int
    size: int
    crc32: int

    @property
    def pack_name(self) -> str:
        return self.pack.name


# --------------------------------------------------------------------------------------
# Indexing
# --------------------------------------------------------------------------------------


def read_pack_index(path: Path) -> Iterator[Entry]:
    """Walk one pack's chunk chain and yield every entry it declares.

    Reads only the chunk tables - a few tens of KB per pack - never the asset data, so
    indexing all 256 packs touches well under 1% of the 13.1 GiB corpus.

    Raises :class:`PackFormatError` if any field contradicts the verified layout. Every
    field predicts where the next one begins, so a wrong layout desyncs inside one entry
    instead of producing plausible nonsense.
    """
    file_length = path.stat().st_size
    if file_length < MIN_PACK_LENGTH:
        raise PackFormatError(f"{path.name}: {file_length} bytes is too short to be a pack")

    with path.open("rb") as fh:
        chunk_offset = 0
        # F2: chunk offsets are absolute and strictly increasing. Requiring progress makes a
        # corrupt or looping `nextChunkOffset` terminate without tracking visited offsets.
        previous_offset = -1

        while True:
            if chunk_offset <= previous_offset:
                raise PackFormatError(
                    f"{path.name}: chunk at 0x{chunk_offset:x} does not advance past "
                    f"0x{previous_offset:x}"
                )
            if chunk_offset + _CHUNK_HEADER.size > file_length:
                raise PackFormatError(
                    f"{path.name}: chunk offset 0x{chunk_offset:x} past end of file"
                )

            previous_offset = chunk_offset
            fh.seek(chunk_offset)
            next_chunk, file_count = _CHUNK_HEADER.unpack(fh.read(_CHUNK_HEADER.size))

            # An entry cannot be shorter than a 1-byte name plus the 16 bytes of fixed
            # fields, so this bounds a `fileCount` that has itself been misread.
            remaining = file_length - fh.tell()
            if file_count > remaining // (_U32.size + 1 + _ENTRY_TAIL.size):
                raise PackFormatError(
                    f"{path.name}: chunk 0x{chunk_offset:x} declares {file_count} files but "
                    f"only {remaining} bytes remain"
                )

            for ordinal in range(file_count):
                yield _read_entry(fh, path, file_length, chunk_offset, ordinal)

            if next_chunk == 0:
                return
            chunk_offset = next_chunk


def _read_entry(fh, path: Path, file_length: int, chunk_offset: int, ordinal: int) -> Entry:
    """Read one ``nameLength / name / offset / size / crc32`` record (F3)."""
    raw = fh.read(_U32.size)
    if len(raw) != _U32.size:
        raise PackFormatError(
            f"{path.name}: chunk 0x{chunk_offset:x} entry {ordinal}: file ended mid-entry"
        )
    (name_length,) = _U32.unpack(raw)

    if name_length == 0 or name_length > MAX_NAME_LENGTH:
        raise PackFormatError(
            f"{path.name}: chunk 0x{chunk_offset:x} entry {ordinal}: implausible name length "
            f"{name_length} (layout desync)"
        )

    name_bytes = fh.read(name_length)
    tail = fh.read(_ENTRY_TAIL.size)
    if len(name_bytes) != name_length or len(tail) != _ENTRY_TAIL.size:
        raise PackFormatError(
            f"{path.name}: chunk 0x{chunk_offset:x} entry {ordinal}: file ended mid-entry"
        )

    # F5: names are plain ASCII. `strict` here is deliberate - a mojibake name would mean the
    # cursor is not where the format says it is.
    try:
        name = name_bytes.decode("ascii")
    except UnicodeDecodeError as exc:
        raise PackFormatError(
            f"{path.name}: chunk 0x{chunk_offset:x} entry {ordinal}: non-ASCII name "
            f"{name_bytes!r} (layout desync)"
        ) from exc

    offset, size, crc = _ENTRY_TAIL.unpack(tail)
    if offset + size > file_length:
        raise PackFormatError(
            f"{path.name}: '{name}' data [{offset}, {offset + size}) runs past the "
            f"{file_length}-byte file"
        )

    return Entry(name=name, pack=path, offset=offset, size=size, crc32=crc)


def find_packs(assets_dir: Path, only: Sequence[str] | None = None) -> list[Path]:
    """Return the pack files to read, sorted by name so runs are reproducible."""
    if only:
        packs = []
        for spec in only:
            candidate = Path(spec)
            if not candidate.is_absolute():
                candidate = assets_dir / spec
            if not candidate.is_file():
                raise FileNotFoundError(f"no such pack: {candidate}")
            packs.append(candidate)
        return packs

    if not assets_dir.is_dir():
        raise FileNotFoundError(f"no such assets directory: {assets_dir}")
    packs = sorted(assets_dir.glob(PACK_GLOB))
    if not packs:
        raise FileNotFoundError(f"no {PACK_GLOB} files under {assets_dir}")
    return packs


def build_index(packs: Iterable[Path], warn=None) -> tuple[list[Entry], list[Path]]:
    """Index every pack.

    Returns ``(entries, failed_packs)``. A malformed pack is reported and skipped whole -
    ``read_pack_index`` is a generator, so its rows are accumulated into a local list and
    only merged once the whole chain has been walked without error. Merging incrementally
    would leave the chunks read *before* a desync in the index and present a truncated
    index as a complete one; callers must treat a non-empty ``failed_packs`` as failure.
    """
    entries: list[Entry] = []
    failed: list[Path] = []
    for pack in packs:
        try:
            pack_entries = list(read_pack_index(pack))
        except PackFormatError as exc:
            if warn is not None:
                warn(str(exc))
            failed.append(pack)
            continue
        entries.extend(pack_entries)
    return entries, failed


def load_index_tsv(path: Path, assets_dir: Path) -> list[Entry]:
    """Read back an index written by the ``index`` subcommand (TSV form)."""
    entries: list[Entry] = []
    with path.open("r", encoding="utf-8") as fh:
        for line in fh:
            if not line.strip() or line.startswith("#"):
                continue
            name, pack, offset, size, crc = line.rstrip("\n").split("\t")
            pack_path = Path(pack)
            if not pack_path.is_absolute():
                pack_path = assets_dir / pack
            entries.append(
                Entry(name, pack_path, int(offset), int(size), int(crc, 0))
            )
    return entries


# --------------------------------------------------------------------------------------
# Reading asset data
# --------------------------------------------------------------------------------------


def _compression_header(fh, entry: Entry) -> int | None:
    """Return the inflated size if this asset carries the zlib preamble, else ``None``.

    F6: detected per asset, never assumed. No asset in the August client has it.
    """
    if entry.size < 8:
        return None
    fh.seek(entry.offset)
    head = fh.read(8)
    magic, inflated = _CHUNK_HEADER.unpack(head)
    return inflated if magic == COMPRESSED_MAGIC else None


def stream_asset(fh, entry: Entry, sink, verify: bool = True) -> int:
    """Copy one asset from an open pack into ``sink.write``, a block at a time.

    Returns the number of bytes written (the inflated length for a compressed asset).
    With ``verify`` the stored CRC-32 is recomputed over the raw bytes as they stream past
    (F4) and a mismatch raises :class:`PackFormatError`; this costs one pass that the copy
    is making anyway, so it is on by default.
    """
    inflated_size = _compression_header(fh, entry)

    running_crc = 0
    written = 0
    decompressor = None
    start = entry.offset
    remaining = entry.size

    if inflated_size is not None:
        # The 8-byte preamble is part of the stored bytes for CRC purposes, so hash it too.
        fh.seek(entry.offset)
        preamble = fh.read(8)
        running_crc = zlib.crc32(preamble, running_crc)
        decompressor = zlib.decompressobj()
        start = entry.offset + 8
        remaining = entry.size - 8

    fh.seek(start)
    while remaining > 0:
        block = fh.read(min(COPY_BLOCK, remaining))
        if not block:
            raise PackFormatError(
                f"{entry.pack_name}: '{entry.name}' ended {remaining} bytes early"
            )
        remaining -= len(block)
        if verify:
            running_crc = zlib.crc32(block, running_crc)
        if decompressor is not None:
            block = decompressor.decompress(block)
        sink.write(block)
        written += len(block)

    if decompressor is not None:
        tail = decompressor.flush()
        sink.write(tail)
        written += len(tail)
        if inflated_size is not None and written != inflated_size:
            raise PackFormatError(
                f"{entry.pack_name}: '{entry.name}' inflated to {written} bytes, header "
                f"declared {inflated_size}"
            )

    if verify and (running_crc & 0xFFFFFFFF) != entry.crc32:
        raise PackFormatError(
            f"{entry.pack_name}: '{entry.name}' CRC-32 mismatch - stored 0x{entry.crc32:08x}, "
            f"computed 0x{running_crc & 0xFFFFFFFF:08x}"
        )

    return written


def verify_existing(target: Path, entry: "Entry") -> str | None:
    """Check that an already-present file on disk really is ``entry``'s asset.

    Returns ``None`` when it matches, else a short human-readable reason. Compares the
    stored size first (cheap) and then CRC-32/ISO-HDLC over the file's bytes against the
    pack's stored ``crc32`` (F4). Only meaningful for uncompressed assets, which is all of
    them in this client (F6); for a compressed entry the stored CRC covers the *stored*
    bytes, not the inflated file, so the size/CRC comparison is skipped and reported as
    unverifiable rather than as a match.
    """
    try:
        stat = target.stat()
    except OSError as exc:
        return f"cannot stat: {exc}"

    with entry.pack.open("rb") as fh:
        if _compression_header(fh, entry) is not None:
            return "stored compressed; cannot verify an existing file against the stored CRC"

    if stat.st_size != entry.size:
        return f"size {stat.st_size:,} B, pack says {entry.size:,} B"

    running = 0
    try:
        with target.open("rb") as fh:
            while True:
                block = fh.read(COPY_BLOCK)
                if not block:
                    break
                running = zlib.crc32(block, running)
    except OSError as exc:
        return f"cannot read: {exc}"

    if (running & 0xFFFFFFFF) != entry.crc32:
        return f"CRC-32 0x{running & 0xFFFFFFFF:08x}, pack says 0x{entry.crc32:08x}"
    return None


class _BytesSink:
    """Collects streamed blocks when the caller genuinely wants the bytes in memory."""

    __slots__ = ("chunks",)

    def __init__(self) -> None:
        self.chunks: list[bytes] = []

    def write(self, block: bytes) -> None:
        self.chunks.append(block)

    def value(self) -> bytes:
        return b"".join(self.chunks)


def read_asset_bytes(entry: Entry, verify: bool = True) -> bytes:
    """Read one asset fully into memory. Only for callers that need the bytes."""
    with entry.pack.open("rb") as fh:
        sink = _BytesSink()
        stream_asset(fh, entry, sink, verify=verify)
        return sink.value()


# --------------------------------------------------------------------------------------
# Selection helpers
# --------------------------------------------------------------------------------------


def matches(entry: Entry, patterns: Sequence[str]) -> bool:
    """Case-insensitive shell glob over the flat asset name (``*`` and ``?``)."""
    lowered = entry.name.lower()
    return any(fnmatch.fnmatchcase(lowered, pattern.lower()) for pattern in patterns)


def select(entries: Sequence[Entry], patterns: Sequence[str]) -> list[Entry]:
    """Entries matching any pattern, de-duplicated by name.

    F5 says names are unique across this client, so the de-duplication never fires here.
    It is kept because a Forgelight client may ship a patch pack that re-declares a name;
    the later pack in sort order is the one the client would load, so the last wins.
    """
    hit: dict[str, Entry] = {}
    for entry in entries:
        if matches(entry, patterns):
            hit[entry.name.lower()] = entry
    return sorted(hit.values(), key=lambda e: e.name.lower())


def safe_output_name(name: str) -> str:
    """Asset names are flat (F5); flatten defensively so nothing can escape the out dir."""
    return name.replace("\\", "_").replace("/", "_").replace("..", "_")


def group_by_pack(entries: Sequence[Entry]) -> list[tuple[Path, list[Entry]]]:
    """Group by pack and sort by offset, so each pack is opened once and read forwards."""
    buckets: dict[Path, list[Entry]] = {}
    for entry in entries:
        buckets.setdefault(entry.pack, []).append(entry)
    return [
        (pack, sorted(items, key=lambda e: e.offset))
        for pack, items in sorted(buckets.items(), key=lambda kv: kv[0].name)
    ]


# --------------------------------------------------------------------------------------
# Subcommands
# --------------------------------------------------------------------------------------


def _warn(message: str) -> None:
    print(f"  ! {message}", file=sys.stderr)


def _gather(args) -> tuple[list[Entry], list[Path]]:
    """Build (or load) the index the other subcommands select from.

    Returns ``(entries, failed_packs)``; ``failed_packs`` is empty for the
    ``--index-file`` path, which reads an index that has already been built.
    """
    if getattr(args, "index_file", None):
        return load_index_tsv(Path(args.index_file), args.assets), []
    packs = find_packs(args.assets, args.pack)
    return build_index(packs, warn=_warn)


def cmd_index(args) -> int:
    packs = find_packs(args.assets, args.pack)
    entries, failed_packs = build_index(packs, warn=_warn)
    entries.sort(key=lambda e: (e.name.lower(), e.pack.name))

    out = Path(args.output) if args.output else None
    if out is not None:
        out.parent.mkdir(parents=True, exist_ok=True)

    if args.format == "json":
        payload = [
            {
                "name": e.name,
                "pack": e.pack_name,
                "offset": e.offset,
                "size": e.size,
                "crc32": e.crc32,
            }
            for e in entries
        ]
        text = json.dumps(payload, indent=1)
        if out is not None:
            out.write_text(text, encoding="utf-8")
        else:
            sys.stdout.write(text + "\n")
    else:
        lines = ["#name\tpack\toffset\tsize\tcrc32"]
        lines += [
            f"{e.name}\t{e.pack_name}\t{e.offset}\t{e.size}\t0x{e.crc32:08x}" for e in entries
        ]
        text = "\n".join(lines) + "\n"
        if out is not None:
            out.write_text(text, encoding="utf-8")
        else:
            sys.stdout.write(text)

    total = sum(e.size for e in entries)
    where = str(out) if out is not None else "stdout"
    print(
        f"{len(entries):,} assets in {len(packs) - len(failed_packs)} packs, "
        f"{total:,} bytes of asset data -> {where}",
        file=sys.stderr,
    )
    # A skipped pack means this index is short by an unknown number of assets; never let a
    # truncated index leave the process with a success status.
    if failed_packs:
        print(
            f"  ! index INCOMPLETE: {len(failed_packs)} of {len(packs)} pack(s) were skipped: "
            + ", ".join(p.name for p in failed_packs),
            file=sys.stderr,
        )
        return 1
    return 0


def cmd_find(args) -> int:
    entries, failed_packs = _gather(args)
    hits = select(entries, args.pattern)
    for e in hits:
        print(f"{e.size:12,}  {e.pack_name:<18}  0x{e.offset:09x}  {e.name}")
    print(f"{len(hits):,} of {len(entries):,} assets matched", file=sys.stderr)
    if failed_packs:
        _warn(f"{len(failed_packs)} pack(s) skipped; this search was not exhaustive")
        return 1
    return 0 if hits else 1


def cmd_extract(args) -> int:
    entries, failed_packs = _gather(args)
    hits = select(entries, args.pattern)
    if not hits:
        print("no asset matched", file=sys.stderr)
        return 1

    out_dir = Path(args.output)
    out_dir.mkdir(parents=True, exist_ok=True)

    written = 0
    failed = 0
    total_bytes = 0
    for pack, items in group_by_pack(hits):
        with pack.open("rb") as fh:
            for entry in items:
                target = out_dir / safe_output_name(entry.name)
                if target.exists() and not args.overwrite:
                    # F4/F5: a pre-existing target is only equivalent to a fresh extraction
                    # if it still IS the asset. Skipping unchecked lets a stale or edited
                    # file masquerade as extracted output, so verify size + CRC-32 and
                    # count a mismatch as a failure rather than a skip.
                    verdict = verify_existing(target, entry)
                    if verdict is None:
                        print(f"  = {entry.name} (exists, verified)", file=sys.stderr)
                        continue
                    if args.no_verify:
                        print(
                            f"  = {entry.name} (exists, skipped; {verdict})", file=sys.stderr
                        )
                        continue
                    _warn(
                        f"{entry.name}: existing {target.name} does not match the pack "
                        f"({verdict}); re-run with --overwrite to replace it"
                    )
                    failed += 1
                    continue
                temp = target.with_suffix(target.suffix + ".partial")
                try:
                    with temp.open("wb") as sink:
                        count = stream_asset(fh, entry, sink, verify=not args.no_verify)
                    temp.replace(target)
                    written += 1
                    total_bytes += count
                    if args.verbose:
                        print(f"  + {entry.name} ({count:,} B) <- {pack.name}", file=sys.stderr)
                except (PackFormatError, OSError, zlib.error) as exc:
                    temp.unlink(missing_ok=True)
                    _warn(f"{entry.name}: {exc}")
                    failed += 1

    print(
        f"wrote {written:,} file(s), {total_bytes:,} bytes -> {out_dir}"
        + (f", {failed} failed" if failed else ""),
        file=sys.stderr,
    )
    # Any requested asset that was not produced (or whose existing file does not match the
    # pack) is a failed extraction, whatever else succeeded: a caller that checks the exit
    # status must be able to conclude "every requested asset is on disk and byte-exact".
    if failed or failed_packs:
        if failed_packs:
            _warn(f"{len(failed_packs)} pack(s) skipped while indexing")
        return 1
    return 0


def cmd_cat(args) -> int:
    entries, _failed_packs = _gather(args)
    hits = select(entries, [args.name])
    if not hits:
        print(f"no asset named '{args.name}'", file=sys.stderr)
        return 1
    if len(hits) > 1:
        print(
            f"'{args.name}' matched {len(hits)} assets; cat takes exactly one",
            file=sys.stderr,
        )
        for e in hits[:10]:
            print(f"  {e.name}", file=sys.stderr)
        return 2

    entry = hits[0]
    with entry.pack.open("rb") as fh:
        if args.output:
            out = Path(args.output)
            out.parent.mkdir(parents=True, exist_ok=True)
            with out.open("wb") as sink:
                count = stream_asset(fh, entry, sink, verify=not args.no_verify)
            print(f"{entry.name}: {count:,} bytes -> {out}", file=sys.stderr)
        else:
            count = stream_asset(fh, entry, sys.stdout.buffer, verify=not args.no_verify)
            sys.stdout.buffer.flush()
            print(f"{entry.name}: {count:,} bytes from {entry.pack_name}", file=sys.stderr)
    return 0


# --------------------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------------------


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="packread",
        description=(
            "Read the August-2017 H1Z1 client's Forgelight v1 .pack archives: index, find, "
            "extract and cat assets. Read-only against the client; streams, so the 13.1 GiB "
            "corpus is never loaded into memory."
        ),
        epilog=(
            "Format facts (verified 2026-08-29 against Assets_*.pack, documented with byte "
            "tables in docs/27-client-pack-format.md): all integers are big-endian u32; a pack "
            "is a linked list of chunks (u32 nextChunkOffset, u32 fileCount) whose entries are "
            "(u32 nameLength, ASCII name, u32 dataOffset, u32 dataSize, u32 crc32); crc32 is "
            "zlib CRC-32 over the stored bytes; no asset in this client is compressed."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument(
        "--assets",
        type=Path,
        default=DEFAULT_ASSETS_DIR,
        help=f"directory holding {PACK_GLOB} (default: {DEFAULT_ASSETS_DIR})",
    )
    parser.add_argument(
        "--pack",
        action="append",
        metavar="FILE",
        help="limit to this pack (repeatable); a bare name is resolved under --assets",
    )

    subs = parser.add_subparsers(dest="command", required=True)

    p_index = subs.add_parser(
        "index",
        help="rebuild the asset index across all packs",
        description="Walk every pack's chunk chain and write name/pack/offset/size/crc32.",
    )
    p_index.add_argument("-o", "--output", help="output file (default: stdout)")
    p_index.add_argument(
        "-f", "--format", choices=("tsv", "json"), default="tsv", help="output format"
    )
    p_index.set_defaults(func=cmd_index)

    p_find = subs.add_parser(
        "find",
        help="locate assets by name pattern",
        description="Print size, pack, offset and name for every asset matching a glob.",
    )
    p_find.add_argument("pattern", nargs="+", help="shell glob over the asset name, e.g. '*.zone'")
    p_find.add_argument(
        "--index-file", help="use a TSV index written by 'index' instead of rescanning the packs"
    )
    p_find.set_defaults(func=cmd_find)

    p_extract = subs.add_parser(
        "extract",
        help="extract named assets to a directory",
        description=(
            "Stream every matching asset out of its pack into --output. A file that is "
            "already present is CRC-checked against the pack instead of being written "
            "again; a mismatch is a failure, not a skip. Exit status is 0 only when every "
            "requested asset is on disk and byte-exact."
        ),
    )
    p_extract.add_argument("pattern", nargs="+", help="asset name or shell glob (repeatable)")
    p_extract.add_argument("-o", "--output", required=True, help="destination directory")
    p_extract.add_argument(
        "--overwrite", action="store_true", help="replace files that already exist"
    )
    p_extract.add_argument(
        "--no-verify",
        action="store_true",
        help="skip the stored CRC-32 check (F4), on write and on an existing file",
    )
    p_extract.add_argument("-v", "--verbose", action="store_true", help="name each file written")
    p_extract.add_argument(
        "--index-file", help="use a TSV index written by 'index' instead of rescanning the packs"
    )
    p_extract.set_defaults(func=cmd_extract)

    p_cat = subs.add_parser(
        "cat",
        help="dump one asset to stdout",
        description="Write exactly one asset's bytes to stdout, or to -o FILE.",
    )
    p_cat.add_argument("name", help="exact asset name (a glob is accepted if it matches one asset)")
    p_cat.add_argument("-o", "--output", help="write to this file instead of stdout")
    p_cat.add_argument(
        "--no-verify", action="store_true", help="skip the stored CRC-32 check (F4)"
    )
    p_cat.add_argument(
        "--index-file", help="use a TSV index written by 'index' instead of rescanning the packs"
    )
    p_cat.set_defaults(func=cmd_cat)

    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        return args.func(args)
    except (FileNotFoundError, PackFormatError) as exc:
        print(f"packread: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
