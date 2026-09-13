#!/usr/bin/env python3
"""
gen-zone-opcodes.py - generate src/Cranberry.Zone/ZoneOpcodes.g.cs from the client's own
packet-id registration table (C:\\Aug2017\\out\\registrations-1148.json, extracted from the 28
registration functions of H1Z1.exe 0.0.118.208059 that call FUN_1413d1df0(id, name, ...)).

The 241 base ids (one registration level) are emitted from that table: the first zone byte
after the gateway tunnel header.

SUB-OPCODE WIDTHS (overhaul plan section 3 lane 2B)

The registration table does NOT say how wide a family's sub-opcode is - it lists members, not
framing - so the width used to be "handled by hand" in each packet class, and the six families
Cranberry writes disagree (1 byte for 0x0f/0x82/0x86, 2 for 0x09/0x11/0xce). The width is,
however, plainly visible in each family's own dispatcher: the decompiled function reads the
base byte, advances the cursor, and reads the selector through a pointer whose TYPE is the
width. So this generator now reads the six dispatcher decompiles other lanes already dumped and
computes the width from the C type at the read site.

Each row of SUB_WIDTHS names the dump, the function, and the ONE decompiled line that performs
the read. The generator asserts that line is still in the dump verbatim (so a re-dump that
moves it fails the build instead of silently changing a width), extracts the pointer variable
or the explicit cast from it, resolves the type from the function's declaration block, and maps
the type to a byte count. No width is typed here.

Usage: python tools/opcodes/gen-zone-opcodes.py [registrations.json] [output.cs] [--dumps DIR]
"""
import json
import re
import sys
from pathlib import Path

argv = [a for a in sys.argv[1:] if not a.startswith("--")]
flags = {a.split("=", 1)[0]: a.split("=", 1)[1] for a in sys.argv[1:] if a.startswith("--") and "=" in a}

src = Path(argv[0] if len(argv) > 0 else r"C:\Aug2017\out\registrations-1148.json")
dst = Path(argv[1] if len(argv) > 1 else
           Path(__file__).resolve().parents[2] / "src" / "Cranberry.Zone" / "ZoneOpcodes.g.cs")
DUMPS = Path(flags.get("--dumps", r"C:\Aug2017\out"))

# ----------------------------------------------------------------------------------------
# The sub-opcode width derivation. Each row is a CITATION, not a value:
#   (base opcode, C# member name, family label, dispatcher, dump path relative to --dumps,
#    the verbatim read line, bytes consumed between the base byte and the sub, why)
# ----------------------------------------------------------------------------------------
SUB_WIDTHS = [
    (0x09, "Command", "cPacketIdCommandBase", "FUN_14129ad10",
     "ghidra-aug/doors-prompt-string/callers_14140c480/FUN_14129ad10_14129ad10.c",
     "(local_1200 = (int)*(short *)pfVar10, pfVar9 <= pfVar1)", 0,
     "the Command dispatcher advances the cursor by 2 ((longlong)pfVar10 + 2U) and reads the "
     "selector through an explicit (short *) cast; case 0x2d is Command.InteractionString"),
    (0x0f, "Character", "cPacketIdCharacterBase", "FUN_140af9ca0",
     "ghidra-aug/character-dispatch/_140af9ca0/FUN_140af9ca0_140af9ca0.c",
     "uVar20 = (uint)*pbVar16;", 0,
     "the Character dispatcher reads one byte at payload+1 and the u64 guid starts at payload+2, "
     "which is what makes RemovePlayer 12 bytes"),
    (0x11, "ClientUpdate", "cPacketIdClientUpdateBase", "FUN_140afc660",
     "ghidra-aug/loot-research/clientupdate-dispatch/_140afc660/FUN_140afc660_140afc660.c",
     "local_17d0 = (uint)*puVar19", 0,
     "the ClientUpdate dispatcher reads a ushort at payload+1; case 0x31 is TextAlert"),
    (0x82, "Weapon", "cPacketIdWeaponBase", "FUN_140b81830",
     "ghidra-aug/fable-character-login/_140b81830_1420cd050/FUN_140b81830_140b81830.c",
     "switch((uint)*pbVar14) {", 4,
     "the weapon family header is u8 base; u32 gameTime; u8 sub - the pre-dispatch filter reads "
     "the u32 at payload+1 (pbVar15 = pbVar14 + 4) before dereferencing the sub as a byte "
     "(docs/84 section 4)"),
    (0x86, "Loadouts", "cPacketIdLoadoutsBase", "FUN_140d37650",
     "inventory-research/loadout-packet/_140b03170/FUN_140d37650_140d37650.c",
     "bVar2 = *pbVar5", 0,
     "the Loadouts handler reads one byte at payload+1 and then gates it with 6 < bVar2 - 1, "
     "i.e. subs 1..7"),
    (0xce, "GameMode", "(unregistered: the 0xce family has no base-id registration)",
     "FUN_140bba510",
     "ghidra-aug/matchflow-b5/_140bba510_14136ddf0_140c9e170_140a2d040_140a3ca40_140f25600"
     "/FUN_140bba510_140bba510.c",
     "local_70 = (uint)*puVar6", 0,
     "the GameMode/BR-HUD dispatcher reads a ushort at payload+1; this family is absent from "
     "registrations-1148.json, so 0xce has no ZoneOpcodes constant of its own"),
]

#: C type at the read site -> bytes. Anything else is a failure, not a guess.
TYPE_WIDTH = {
    "byte": 1, "char": 1, "undefined1": 1, "uchar": 1,
    "short": 2, "ushort": 2, "undefined2": 2,
    "int": 4, "uint": 4, "undefined4": 4,
}


def derive_sub_width(row):
    """Compute one family's sub width from its dispatcher decompile. Never returns a guess."""
    _opcode, _member, _family, function, dump_rel, evidence, _prefix, _why = row
    dump = DUMPS / dump_rel
    if not dump.is_file():
        raise SystemExit(f"[gen-zone-opcodes] missing dispatcher dump {dump}")
    text = dump.read_text(encoding="utf-8", errors="replace")
    if text.count(evidence) != 1:
        raise SystemExit(
            f"[gen-zone-opcodes] {function}: the cited read line is not in {dump_rel} exactly "
            f"once ({text.count(evidence)} hit(s)) - re-derive the width before trusting it:\n"
            f"    {evidence}")

    # An explicit cast at the read site wins: `*(short *)pfVar10`.
    cast = re.search(r"\*\((\w+) \*\)\w+", evidence)
    if cast:
        ctype = cast.group(1)
    else:
        deref = re.search(r"\*(\w+)", evidence)
        if not deref:
            raise SystemExit(f"[gen-zone-opcodes] {function}: no dereference in the cited line")
        variable = deref.group(1)
        decl = re.search(rf"^\s*(\w+)\s+\*{variable};\s*$", text, re.MULTILINE)
        if not decl:
            raise SystemExit(
                f"[gen-zone-opcodes] {function}: {dump_rel} declares no `T *{variable};`")
        ctype = decl.group(1)

    if ctype not in TYPE_WIDTH:
        raise SystemExit(f"[gen-zone-opcodes] {function}: unmapped C type {ctype!r}")
    return ctype, TYPE_WIDTH[ctype]

rows = json.load(open(src, encoding="utf-8"))
BASE_REGISTRAR = "FUN_1413bfa90"
base = {}
aliases = {}
# The base registrar names every base id; a family registrar may register its own member with
# the same single-level id (e.g. 0x93 ProfileStatsBase vs cProfileStatsPacketIdGetPlayerProfileStats).
# The base registrar wins; the family name is kept as an alias comment.
for r in sorted(rows, key=lambda r: 0 if r["registeredIn"] == BASE_REGISTRAR else 1):
    if len(r["levels"]) != 1:
        continue
    opcode = r["levels"][0]
    name = r["member"]
    name = re.sub(r"^cPacketId", "", name)
    name = re.sub(r"^c(\w+?)PacketIdNone$", r"\1Base", name)   # e.g. cCharacterPacketIdNone -> CharacterBase
    if opcode in base:
        if base[opcode] != name:
            aliases.setdefault(opcode, []).append(name)
        continue
    base[opcode] = name

widths = [(row, *derive_sub_width(row)) for row in SUB_WIDTHS]

lines = [
    "// <auto-generated>",
    f"// Generated by tools/opcodes/gen-zone-opcodes.py from {src.name} (the client's own",
    "// packet-id registrations, FUN_1413bfa90 and the 27 family registrars) and, for the",
    "// sub-opcode widths, from six family dispatcher decompiles under C:\\Aug2017\\out.",
    "// Do not edit.",
    "// </auto-generated>",
    "",
    "#nullable enable",
    "",
    "namespace Cranberry.Zone;",
    "",
    "/// <summary>",
    "/// One family's sub-opcode framing, read off that family's own dispatcher.",
    "/// </summary>",
    "/// <param name=\"BaseOpcode\">The first zone byte.</param>",
    "/// <param name=\"Family\">The client's registered family name, where it has one.</param>",
    "/// <param name=\"Bytes\">How wide the sub-opcode is: 1 or 2.</param>",
    "/// <param name=\"PrefixBytes\">Bytes between the base byte and the sub (0 everywhere but"
    " <c>0x82</c>, whose header is <c>u8 base; u32 gameTime; u8 sub</c>).</param>",
    "/// <param name=\"Dispatcher\">The client function the width was read from.</param>",
    "/// <param name=\"Dump\">The decompile it was read in, under <c>C:\\Aug2017\\out</c>.</param>",
    "/// <param name=\"Evidence\">The decompiled line that performs the read.</param>",
    "public readonly record struct ZoneSubOpcodeWidth(",
    "    byte BaseOpcode, string Family, int Bytes, int PrefixBytes,",
    "    string Dispatcher, string Dump, string Evidence);",
    "",
    "/// <summary>ClientProtocol_1148 base opcodes: the first byte of a zone packet.</summary>",
    "public static class ZoneOpcodes",
    "{",
]
for opcode in sorted(base):
    alias = f"   // also registered as {', '.join(aliases[opcode])}" if opcode in aliases else ""
    lines.append(f"    public const byte {base[opcode]} = 0x{opcode:02x};{alias}")
lines += [
    "",
    "    private static readonly string?[] NamesById = BuildNames();",
    "",
    "    /// <summary>The client's registered name for a base opcode, or null when none is registered.</summary>",
    "    public static string? Name(byte opcode) => NamesById[opcode];",
    "",
    "    private static string?[] BuildNames()",
    "    {",
    "        var names = new string?[256];",
]
for opcode in sorted(base):
    lines.append(f"        names[0x{opcode:02x}] = \"{base[opcode]}\";")
lines += [
    "        return names;",
    "    }",
    "",
    "    /// <summary>",
    "    /// How wide each family's sub-opcode is, read off that family's own dispatcher.",
    "    /// <para>",
    "    /// The registration table lists members, not framing, so it cannot answer this; the",
    "    /// dispatchers can, and do. Every number below is computed by",
    "    /// <c>tools/opcodes/gen-zone-opcodes.py</c> from the C type at the read site in the cited",
    "    /// decompile - a <c>byte *</c> is 1, a <c>ushort *</c> or an explicit <c>(short *)</c> cast",
    "    /// is 2 - and the tool fails rather than guessing if the cited line moves.",
    "    /// </para>",
    "    /// </summary>",
    "    public static class SubOpcodeWidth",
    "    {",
]
for (opcode, member, family, function, dump_rel, evidence, prefix, why), ctype, width in widths:
    doc_why = why.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    doc_why = doc_why[:1].upper() + doc_why[1:] + "."
    lines += [
        "        /// <summary>",
        f"        /// <c>0x{opcode:02x}</c> {family}: <b>{width}</b> byte sub-opcode"
        + (f", after {prefix} prefix byte(s)" if prefix else "") + ".",
        f"        /// Read as <c>{ctype} *</c> in <c>{function}</c>",
        f"        /// (<c>{dump_rel}</c>).",
        "        /// <para>",
    ]
    words, line_buf = doc_why.split(), ""
    for word in words:
        if line_buf and len(line_buf) + 1 + len(word) > 92:
            lines.append(f"        /// {line_buf}")
            line_buf = word
        else:
            line_buf = f"{line_buf} {word}".strip()
    if line_buf:
        lines.append(f"        /// {line_buf}")
    lines += [
        "        /// </para>",
        "        /// </summary>",
        f"        public const int {member} = {width};",
        "",
    ]
    if prefix:
        lines += [
            f"        /// <summary>Bytes between the <c>0x{opcode:02x}</c> base byte and its sub-opcode.</summary>",
            f"        public const int {member}PrefixBytes = {prefix};",
            "",
        ]

lines += [
    "        /// <summary>Every derived width, with the decompile each one was read in.</summary>",
    "        public static readonly IReadOnlyList<ZoneSubOpcodeWidth> Derived =",
    "        [",
]
for (opcode, _member, family, function, dump_rel, evidence, prefix, _why), _ctype, width in widths:
    esc = lambda s: s.replace("\\", "\\\\").replace("\"", "\\\"")  # noqa: E731
    lines.append(
        f"            new(0x{opcode:02x}, \"{esc(family)}\", {width}, {prefix}, "
        f"\"{esc(function)}\", \"{esc(dump_rel)}\", \"{esc(evidence)}\"),")
lines += [
    "        ];",
    "",
    "        /// <summary>The sub-opcode width of a family, or null when it has not been derived.</summary>",
    "        public static int? For(byte baseOpcode)",
    "        {",
    "            foreach (ZoneSubOpcodeWidth row in Derived)",
    "            {",
    "                if (row.BaseOpcode == baseOpcode)",
    "                {",
    "                    return row.Bytes;",
    "                }",
    "            }",
    "",
    "            return null;",
    "        }",
    "    }",
    "}",
    "",
]
dst.write_text("\n".join(lines), encoding="utf-8", newline="\n")
print(f"[gen-zone-opcodes] {len(base)} base opcodes, {len(widths)} derived sub widths "
      f"({', '.join(f'0x{r[0][0]:02x}={r[2]}' for r in widths)}) -> {dst}")
