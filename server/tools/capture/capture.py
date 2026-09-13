#!/usr/bin/env python3
r"""
capture.py - read C:\Aug2017\captures\wire-*.txt into structured records and answer questions
about them.

The capture file is written by src/Cranberry.Host/FilePacketRecorder.cs, one line per record:

    # time | remote | protocol | direction | length | bytes
    HH:MM:SS.fff | ip:port | LoginUdp_14 | session-request | 0 | crc=3 id=... udp=512
    HH:MM:SS.fff | ip:port | LoginUdp_14 | c2s-raw@<keystream pos> | <n> | <hex ciphertext>
    HH:MM:SS.fff | ip:port | LoginUdp_14 | c2s | <n> | <hex plaintext application message>
    HH:MM:SS.fff | ip:port | ExternalGatewayApi_3 | s2c | <n> | <hex plaintext>

SOE framing (session/ack/fragment/multi) is already stripped by the recorder: c2s and s2c are
COMPLETE application messages. c2s-raw@N is the same c2s message before RC4, with N the inbound
keystream position, so a raw/plain pair is also the RC4 arming evidence.

Layering applied here:
  LoginUdp_14          -> byte 0 is a login opcode (docs/03).
  ExternalGatewayApi_3 -> byte 0 is the gateway header: opcode = b & 0x1F, channel = b >> 5
                          (src/Cranberry.Zone/GatewayPackets.cs). Opcode 6 = client tunnel,
                          5 = server tunnel; the rest of the message is ClientProtocol bytes.
  CHANNELS 2 AND 3 ARE NOT DECODED AS OPCODES. ZoneService.HandleClientTunnel diverts them
  before base-opcode dispatch (FUN_140dd1400): they are opcode-free movement streams, and
  decoding them as base opcodes manufactures tens of thousands of phantom packets.

Subcommands:
  list     one line per record, with resolved names, wall clock and inter-packet gaps
  seq      the same, collapsed to a behavioural sequence (runs of one opcode folded)
  diff     compare two sessions' collapsed sequences
  family   dump every packet of one opcode family, with hex
  summary  per-protocol/direction/channel counts and first occurrences
  latency  for each s2c opcode, what the client sent next and how long it took
"""
from __future__ import annotations

import argparse
import os
import sys
from dataclasses import dataclass
from typing import Dict, Iterable, Iterator, List, Optional, Sequence, Tuple

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from opcodes import GATEWAY_OPCODES, LOGIN_OPCODES, Opcode, OpcodeTable, parse_opcode_selector

CAPTURE_DIR = r"C:\Aug2017\captures"

LOGIN_PROTOCOL = "LoginUdp_14"
GATEWAY_PROTOCOL = "ExternalGatewayApi_3"

# ZoneService.HandleClientTunnel: channel 2 = local player movement, channel 3 = managed objects.
MOVEMENT_CHANNELS = (2, 3)
MOVEMENT_CHANNEL_NAMES = {2: "PlayerMovement", 3: "ManagedMovement"}


@dataclass
class Record:
    index: int
    time: float                 # seconds since midnight, from the capture's own clock
    time_text: str
    remote: str
    protocol: str
    direction: str              # session-request | c2s | s2c | c2s-raw
    keystream: Optional[int]    # only for c2s-raw
    length: int
    payload: bytes              # empty for session-request
    note: str = ""              # session-request text
    # layered view, filled by classify()
    kind: str = ""              # session | login | gateway | tunnel | movement
    channel: Optional[int] = None
    gateway_opcode: Optional[int] = None
    opcode: Optional[Opcode] = None
    label: str = ""

    @property
    def is_client(self) -> bool:
        return self.direction.startswith("c2s")

    @property
    def body(self) -> bytes:
        """Application bytes after the opcode: past the gateway header for a tunnelled zone
        message, past the login opcode for a LoginUdp_14 one. Empty for anything unlayered."""
        if self.kind == "tunnel" and self.opcode is not None:
            return self.payload[1 + self.opcode.header_len:]
        if self.kind == "login":
            return self.payload[1:]
        return b""


def parse_time(text: str) -> float:
    hh, mm, rest = text.split(":")
    return int(hh) * 3600 + int(mm) * 60 + float(rest)


def resolve_path(name: str) -> str:
    if os.path.exists(name):
        return name
    candidate = os.path.join(CAPTURE_DIR, name)
    if os.path.exists(candidate):
        return candidate
    if not name.startswith("wire-"):
        candidate = os.path.join(CAPTURE_DIR, "wire-" + name + ".txt")
        if os.path.exists(candidate):
            return candidate
    raise SystemExit("no such capture: " + name)


def read_records(path: str, table: OpcodeTable, want_raw: bool = True) -> List[Record]:
    return list(iter_records(path, table, want_raw))


def iter_records(path: str, table: OpcodeTable, want_raw: bool = True) -> Iterator[Record]:
    index = 0
    day_offset = 0.0
    previous = None
    with open(path, "r", encoding="utf-8", errors="replace") as handle:
        for line in handle:
            if not line or line.startswith("#"):
                continue
            parts = line.rstrip("\n").split(" | ")
            if len(parts) < 6:
                continue
            time_text, remote, protocol = parts[0], parts[1], parts[2]
            direction, length_text, blob = parts[3], parts[4], parts[5]
            keystream = None
            if direction.startswith("c2s-raw@"):
                keystream = int(direction.split("@", 1)[1])
                direction = "c2s-raw"
                if not want_raw:
                    continue
            seconds = parse_time(time_text)
            if previous is not None and seconds + day_offset < previous - 3600:
                day_offset += 86400.0        # a capture that crosses midnight
            seconds += day_offset
            previous = seconds
            note = ""
            payload = b""
            if direction == "session-request":
                note = blob
            else:
                try:
                    payload = bytes.fromhex(blob)
                except ValueError:
                    note = blob
            index += 1
            record = Record(
                index=index, time=seconds, time_text=time_text, remote=remote,
                protocol=protocol, direction=direction, keystream=keystream,
                length=int(length_text), payload=payload, note=note)
            classify(record, table)
            yield record


def classify(record: Record, table: OpcodeTable) -> None:
    if record.direction == "c2s-raw":
        # Ciphertext. There is nothing to layer: byte 0 is keystream, not a gateway header.
        record.kind = "raw"
        record.label = "ciphertext"
        return

    if record.direction == "session-request":
        record.kind = "session"
        record.label = "SessionRequest(" + record.note + ")"
        return

    if record.protocol == LOGIN_PROTOCOL:
        record.kind = "login"
        if record.payload:
            opcode = record.payload[0]
            record.gateway_opcode = opcode
            record.label = LOGIN_OPCODES.get(opcode, "login 0x%02x" % opcode)
            if opcode in (0x10, 0x11) and len(record.payload) > 13:
                # docs/03: u8 op; u64 serverId; u32 length; payload - the tunnelled zone bytes.
                record.opcode = table.resolve(record.payload[13:])
                record.label += " -> " + record.opcode.name
        else:
            record.label = "empty"
        return

    if record.protocol == GATEWAY_PROTOCOL:
        if not record.payload:
            record.kind = "gateway"
            record.label = "empty"
            return
        header = record.payload[0]
        opcode = header & 0x1F
        channel = header >> 5
        record.gateway_opcode = opcode
        record.channel = channel
        if opcode in (5, 6):
            if channel in MOVEMENT_CHANNELS:
                # Opaque by construction. No opcode is resolved and none should be.
                record.kind = "movement"
                record.label = "ch%d %s" % (channel, MOVEMENT_CHANNEL_NAMES[channel])
                return
            record.kind = "tunnel"
            record.opcode = table.resolve(record.payload[1:])
            record.label = record.opcode.name
            if channel:
                record.label = "ch%d %s" % (channel, record.label)
            return
        record.kind = "gateway"
        record.label = GATEWAY_OPCODES.get(opcode, "gateway op %d" % opcode)
        if channel:
            record.label += " (ch%d)" % channel
        return

    record.kind = "unknown"
    record.label = record.protocol + " " + record.payload[:1].hex()


def signature(record: Record) -> str:
    """The identity used for collapsing and diffing: direction + label, no payload."""
    return record.direction + " " + record.protocol[:1] + ":" + record.label


# ---------------------------------------------------------------------------- reporting

def fmt_gap(gap: Optional[float]) -> str:
    if gap is None:
        return "      "
    if gap >= 1.0:
        return "%6.2f" % gap
    return "%5.0fm" % (gap * 1000)


def fmt_clock(seconds: float) -> str:
    seconds %= 86400
    return "%02d:%02d:%06.3f" % (seconds // 3600, (seconds // 60) % 60, seconds % 60)


def want(record: Record, args) -> bool:
    if getattr(args, "from_", None) and record.time_text < args.from_:
        return False
    if getattr(args, "to", None) and record.time_text > args.to:
        return False
    if getattr(args, "direction", None) and record.direction != args.direction:
        return False
    if getattr(args, "protocol", None) and not record.protocol.lower().startswith(args.protocol.lower()):
        return False
    if getattr(args, "channel", None) is not None and record.channel != args.channel:
        return False
    if getattr(args, "no_movement", False) and record.kind == "movement":
        return False
    if getattr(args, "grep", None) and args.grep.lower() not in record.label.lower():
        return False
    return True


def cmd_list(args) -> None:
    table = OpcodeTable(args.registrations)
    path = resolve_path(args.capture)
    previous = None
    for record in iter_records(path, table, want_raw=args.raw):
        if not want(record, args):
            continue
        gap = None if previous is None else record.time - previous
        previous = record.time
        head = record.payload[:args.bytes_].hex().upper()
        if len(record.payload) > args.bytes_:
            head += ".."
        extra = (" ks@%d" % record.keystream) if record.keystream is not None else ""
        print("%6d %s %s %6s %-8s %7d %s%s  %s" % (
            record.index, record.time_text, fmt_gap(gap), record.remote.split(":")[-1],
            record.direction, record.length, record.label, extra, head))


def collapse(records: Iterable[Record]) -> List[Tuple[Record, int, float]]:
    """Fold consecutive records with the same signature. Returns (first, count, span)."""
    out: List[Tuple[Record, int, float]] = []
    current: Optional[Record] = None
    count = 0
    last_time = 0.0
    for record in records:
        if current is not None and signature(record) == signature(current):
            count += 1
            last_time = record.time
            continue
        if current is not None:
            out.append((current, count, last_time - current.time))
        current = record
        count = 1
        last_time = record.time
    if current is not None:
        out.append((current, count, last_time - current.time))
    return out


def cmd_seq(args) -> None:
    table = OpcodeTable(args.registrations)
    path = resolve_path(args.capture)
    records = [r for r in iter_records(path, table, want_raw=False) if want(r, args)]
    previous = None
    for first, count, span in collapse(records):
        gap = None if previous is None else first.time - previous
        previous = first.time + span
        run = (" x%d" % count) if count > 1 else ""
        if count > 1 and span > 0:
            run += " over %.2fs" % span
        print("%6d %s %s %-8s %7d %s%s" % (
            first.index, first.time_text, fmt_gap(gap), first.direction,
            first.length, first.label, run))


def cmd_diff(args) -> None:
    import difflib
    table = OpcodeTable(args.registrations)
    def side(path, lo, hi):
        return [r for r in iter_records(resolve_path(path), table, want_raw=False)
                if want(r, args)
                and (lo is None or r.time_text >= lo) and (hi is None or r.time_text <= hi)]

    left = side(args.left, args.left_from, args.left_to)
    right = side(args.right, args.right_from, args.right_to)
    left_c = collapse(left)
    right_c = collapse(right)
    a = [signature(r) + ((" x%d" % c) if args.counts and c > 1 else "") for r, c, _ in left_c]
    b = [signature(r) + ((" x%d" % c) if args.counts and c > 1 else "") for r, c, _ in right_c]
    matcher = difflib.SequenceMatcher(a=a, b=b, autojunk=False)
    print("--- %s  (%d records, %d runs)" % (os.path.basename(args.left), len(left), len(a)))
    print("+++ %s  (%d records, %d runs)" % (os.path.basename(args.right), len(right), len(b)))
    for tag, i1, i2, j1, j2 in matcher.get_opcodes():
        if tag == "equal":
            if i2 - i1 > args.context * 2:
                for k in range(i1, i1 + args.context):
                    print("  " + a[k])
                print("  ... %d identical run(s) ..." % (i2 - i1 - 2 * args.context))
                for k in range(i2 - args.context, i2):
                    print("  " + a[k])
            else:
                for k in range(i1, i2):
                    print("  " + a[k])
            continue
        for k in range(i1, i2):
            print("- %s   @%s" % (a[k], left_c[k][0].time_text))
        for k in range(j1, j2):
            print("+ %s   @%s" % (b[k], right_c[k][0].time_text))


def cmd_family(args) -> None:
    table = OpcodeTable(args.registrations)
    levels = parse_opcode_selector(args.opcode, table)
    path = resolve_path(args.capture)
    matches = 0
    previous = None
    for record in iter_records(path, table, want_raw=False):
        if record.opcode is None:
            continue
        if record.opcode.levels[:len(levels)] != levels:
            continue
        if not want(record, args):
            continue
        matches += 1
        gap = None if previous is None else record.time - previous
        previous = record.time
        blob = record.payload[:args.bytes_].hex().upper()
        if len(record.payload) > args.bytes_:
            blob += ".."
        print("%6d %s %s %-8s %7d %s [%s/%s]  %s" % (
            record.index, record.time_text, fmt_gap(gap), record.direction, record.length,
            record.opcode.name, record.opcode.hex, record.opcode.sub_width, blob))
        if args.limit and matches >= args.limit:
            break
    sys.stderr.write("# %d packet(s) in family %s\n" % (matches, args.opcode))


def cmd_summary(args) -> None:
    table = OpcodeTable(args.registrations)
    path = resolve_path(args.capture)
    counts: Dict[str, int] = {}
    bytes_by: Dict[str, int] = {}
    first_seen: Dict[str, Tuple[str, int]] = {}
    ports: Dict[str, List[float]] = {}
    start = end = None
    total = 0
    for record in iter_records(path, table, want_raw=True):
        total += 1
        if start is None:
            start = record.time
        end = record.time
        key = record.protocol + " " + record.direction
        if record.channel:
            key += " ch%d" % record.channel
        counts[key] = counts.get(key, 0) + 1
        bytes_by[key] = bytes_by.get(key, 0) + record.length
        ports.setdefault(record.remote, []).append(record.time)
        if record.direction in ("c2s", "s2c"):
            label = record.direction + " " + record.label
            if label not in first_seen:
                first_seen[label] = (record.time_text, record.index)
    print("# " + os.path.basename(path))
    if start is None:
        print("# empty")
        return
    print("# %d records, %.1fs wall clock" % (total, end - start))
    print("\n## channels")
    for key in sorted(counts):
        print("  %-44s %7d  %12sB" % (key, counts[key], format(bytes_by[key], ",")))
    print("\n## endpoints")
    for remote, times in sorted(ports.items(), key=lambda kv: kv[1][0]):
        print("  %-24s %7d records  %s .. %s" % (
            remote, len(times), fmt_clock(times[0]), fmt_clock(times[-1])))
    print("\n## first occurrence of each message")
    for label, (when, index) in sorted(first_seen.items(), key=lambda kv: kv[1][1]):
        print("  %s #%-6d %s" % (when, index, label))


def cmd_latency(args) -> None:
    """For every server message, the next client message and the delay. Real reply timings."""
    table = OpcodeTable(args.registrations)
    path = resolve_path(args.capture)
    records = [r for r in iter_records(path, table, want_raw=False)
               if r.direction in ("c2s", "s2c") and want(r, args)]
    pairs: Dict[Tuple[str, str], List[float]] = {}
    for i, record in enumerate(records):
        if record.direction != "s2c":
            continue
        for follow in records[i + 1:]:
            if follow.direction == "c2s":
                pairs.setdefault((record.label, follow.label), []).append(follow.time - record.time)
                break
    rows = sorted(pairs.items(), key=lambda kv: -len(kv[1]))
    print("%-44s %-44s %5s %8s %8s %8s" % (
        "server message", "-> next client message", "n", "min", "med", "max"))
    for (s2c_label, c2s_label), deltas in rows:
        if len(deltas) < args.min_count:
            continue
        deltas.sort()
        med = deltas[len(deltas) // 2]
        print("%-44s %-44s %5d %7.0fm %7.0fm %7.0fm" % (
            s2c_label[:43], c2s_label[:43], len(deltas),
            deltas[0] * 1000, med * 1000, deltas[-1] * 1000))


def add_filters(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--from", dest="from_", metavar="HH:MM:SS",
                        help="drop records before this wall clock (string compare, so a prefix works)")
    parser.add_argument("--to", metavar="HH:MM:SS", help="drop records after this wall clock")
    parser.add_argument("--direction", choices=["c2s", "s2c", "c2s-raw", "session-request"])
    parser.add_argument("--protocol", help="prefix match, e.g. Login or External")
    parser.add_argument("--channel", type=int)
    parser.add_argument("--no-movement", action="store_true",
                        help="drop the opaque channel 2/3 streams")
    parser.add_argument("--grep", help="substring match on the resolved label")


def main(argv: Sequence[str]) -> None:
    parser = argparse.ArgumentParser(prog="capture.py", description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--registrations", default=None)
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("list", help="one line per record")
    p.add_argument("capture")
    p.add_argument("--bytes", dest="bytes_", type=int, default=32)
    p.add_argument("--raw", action="store_true", help="include c2s-raw ciphertext rows")
    add_filters(p)
    p.set_defaults(func=cmd_list)

    p = sub.add_parser("seq", help="collapsed behavioural sequence")
    p.add_argument("capture")
    add_filters(p)
    p.set_defaults(func=cmd_seq)

    p = sub.add_parser("diff", help="compare two sessions")
    p.add_argument("left")
    p.add_argument("right")
    p.add_argument("--context", type=int, default=3)
    p.add_argument("--counts", action="store_true", help="treat run lengths as significant")
    p.add_argument("--left-from", metavar="HH:MM:SS")
    p.add_argument("--left-to", metavar="HH:MM:SS")
    p.add_argument("--right-from", metavar="HH:MM:SS")
    p.add_argument("--right-to", metavar="HH:MM:SS")
    add_filters(p)
    p.set_defaults(func=cmd_diff)

    p = sub.add_parser("family", help="dump one opcode family")
    p.add_argument("capture")
    p.add_argument("opcode", help="0f, 0f:45, or a registered name")
    p.add_argument("--bytes", dest="bytes_", type=int, default=64)
    p.add_argument("--limit", type=int, default=0)
    add_filters(p)
    p.set_defaults(func=cmd_family)

    p = sub.add_parser("summary", help="counts and first occurrences")
    p.add_argument("capture")
    p.set_defaults(func=cmd_summary)

    p = sub.add_parser("latency", help="server message -> next client message timings")
    p.add_argument("capture")
    p.add_argument("--min-count", type=int, default=1)
    add_filters(p)
    p.set_defaults(func=cmd_latency)

    args = parser.parse_args(argv)
    if args.registrations is None:
        from opcodes import DEFAULT_REGISTRATIONS
        args.registrations = DEFAULT_REGISTRATIONS
    args.func(args)


if __name__ == "__main__":
    try:
        main(sys.argv[1:])
    except BrokenPipeError:
        pass
