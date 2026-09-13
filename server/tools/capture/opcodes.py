#!/usr/bin/env python3
r"""
opcodes.py - name resolution for ClientProtocol_1148 zone opcodes and the LoginUdp_14
login opcodes.

Zone names come from the client's OWN packet-id registration table,
C:\Aug2017\out\registrations-1148.json (1,743 rows extracted from the 28 registration
functions of H1Z1.exe 0.0.118.208059). Nothing here is invented: a byte with no
registration is reported as "unregistered", never guessed.

Sub-opcode WIDTH is not in the registration table (tools/opcodes/gen-zone-opcodes.py says so
in as many words: "Sub-opcode widths differ per family and are handled by hand"). This module
therefore resolves a two-level id by trying the u16 little-endian sub first and the u8 sub
second, and reports which width matched so a reader can tell a confident join from a lucky one.
"""
from __future__ import annotations

import json
import os
from typing import Dict, List, Optional, Tuple

DEFAULT_REGISTRATIONS = r"C:\Aug2017\out\registrations-1148.json"

# docs/03: the LoginUdp_14 dispatcher switches on the first byte. Requests are odd, replies even.
LOGIN_OPCODES: Dict[int, str] = {
    0x01: "LoginRequest",
    0x02: "LoginReply",
    0x03: "Logout",
    0x04: "ForcedDisconnect",
    0x05: "CharacterCreateRequest",
    0x06: "CharacterCreateReply",
    0x07: "CharacterLoginRequest",
    0x08: "CharacterLoginReply",
    0x09: "CharacterDeleteRequest",
    0x0A: "CharacterDeleteReply",
    0x0B: "CharacterSelectInfoRequest",
    0x0C: "CharacterSelectInfoReply",
    0x0D: "ServerListRequest",
    0x0E: "ServerListReply",
    0x0F: "ServerUpdate",
    0x10: "TunnelAppPacketClientToServer",
    0x11: "TunnelAppPacketServerToClient",
    0x12: "CharacterTransferRequest",
    0x13: "CharacterTransferReply",
}

# src/Cranberry.Zone/GatewayPackets.cs: the header byte packs a five-bit opcode below a
# three-bit channel.
GATEWAY_OPCODES: Dict[int, str] = {
    1: "Gateway.LoginRequest",
    2: "Gateway.LoginReply",
    3: "Gateway.Logout",
    5: "Gateway.TunnelToClient",
    6: "Gateway.TunnelFromClient",
}


class OpcodeTable:
    """Registration rows indexed by their level list."""

    def __init__(self, path: str = DEFAULT_REGISTRATIONS):
        self.path = path
        self.rows: List[dict] = []
        self.by_levels: Dict[Tuple[int, ...], dict] = {}
        if os.path.exists(path):
            with open(path, "r", encoding="utf-8") as handle:
                self.rows = json.load(handle)
            for row in self.rows:
                key = tuple(row["levels"])
                # The base registrar (FUN_1413bfa90) wins a collision; family aliases lose.
                if key in self.by_levels and row.get("registeredIn") != "FUN_1413bfa90":
                    continue
                self.by_levels[key] = row

    @staticmethod
    def _short(row: dict) -> str:
        name = row["member"]
        for prefix in ("cPacketId", "cCharacterPacket", "cClientUpdatePacketId",
                       "cCommandPacketId", "cInventoryPacketId", "cAbilityPacketId"):
            if name.startswith(prefix):
                return name[len(prefix):]
        if name.startswith("c") and "PacketId" in name:
            return name.split("PacketId", 1)[1] or name
        return name

    def base_name(self, opcode: int) -> Optional[str]:
        row = self.by_levels.get((opcode,))
        return self._short(row) if row else None

    def resolve(self, payload: bytes) -> "Opcode":
        """Resolve a zone payload's opcode. Never guesses past the registration table."""
        if not payload:
            return Opcode((), "empty", 0, "none")
        base = payload[0]
        base_row = self.by_levels.get((base,))
        base_name = self._short(base_row) if base_row else None

        if len(payload) >= 3:
            sub16 = payload[1] | (payload[2] << 8)
            row = self.by_levels.get((base, sub16))
            if row is not None and (payload[2] != 0 or (base, payload[1]) not in self.by_levels):
                return Opcode((base, sub16), self._short(row), 3, "u16")
        if len(payload) >= 2:
            sub8 = payload[1]
            row = self.by_levels.get((base, sub8))
            if row is not None:
                # Both widths agree whenever byte 2 is zero; say so rather than pretend.
                width = "u8" if len(payload) < 3 or payload[2] != 0 else "u8|u16"
                return Opcode((base, sub8), self._short(row), 2, width)
        if base_name is not None:
            return Opcode((base,), base_name, 1, "u8")
        return Opcode((base,), f"unregistered 0x{base:02x}", 1, "u8")


class Opcode:
    __slots__ = ("levels", "name", "header_len", "sub_width")

    def __init__(self, levels: Tuple[int, ...], name: str, header_len: int, sub_width: str):
        self.levels = levels
        self.name = name
        self.header_len = header_len
        self.sub_width = sub_width

    @property
    def hex(self) -> str:
        return " ".join(f"{level:02x}" if level < 256 else f"{level:04x}" for level in self.levels)

    def __str__(self) -> str:
        return f"{self.name} [{self.hex}]"

    def __eq__(self, other) -> bool:
        return isinstance(other, Opcode) and self.levels == other.levels

    def __hash__(self) -> int:
        return hash(self.levels)


def parse_opcode_selector(text: str, table: OpcodeTable) -> Tuple[int, ...]:
    """Accept '0f', '0f:45', '0f.45', 'FullCharacterDataRequest' or 'CharacterBase'."""
    cleaned = text.strip()
    parts = [p for p in cleaned.replace(".", ":").replace("/", ":").split(":") if p]
    try:
        return tuple(int(p, 16) for p in parts)
    except ValueError:
        pass
    lowered = cleaned.lower()
    for levels, row in table.by_levels.items():
        if OpcodeTable._short(row).lower() == lowered or row["member"].lower() == lowered:
            return levels
    raise SystemExit(f"no opcode matches {text!r}")
