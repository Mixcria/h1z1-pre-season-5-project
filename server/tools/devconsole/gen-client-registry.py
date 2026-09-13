#!/usr/bin/env python3
r"""
gen-client-registry.py - generate src/Cranberry.Zone/DevConsole/ClientRegistry1148.g.cs from the
census of the August client's own CVar/command registry.

Why the file exists
-------------------
When the server pushes a console name with Command.AddWorldCommand (09 40), the client's handler
FUN_141280280 asks its global registry whether that name is already taken (FUN_141e9ac10 ->
FUN_141e9d950 -> FUN_141e9d680).  On a hit it writes

    Server sent %s (%s) that conflicts with a local command

to Client\Logs\AdminCommands.log, registers nothing, and the name is then un-typeable: the client
emits no packet at all for it.  The lookup matches by HASH and returns without comparing the name
whenever the entry's name pointer (+0x28) is null - which is the case for all 975 static CVars in
the image - so the collision oracle is a hash-set membership test, and that set is what this file
generates.  CommandRegistry.Register refuses a name whose CommandHash is in it, which is how a new
verb can never silently become un-typeable on the client.

The input
---------
out\devconsole-20260901\ghidra\client-registry-1148.tsv - 1,025 rows "hash \t name \t kind \t
evidence" consolidated by the design lane on 2026-09-02 from headless Ghidra runs over H1Z1.exe
0.0.118.208059 (read-only, no analysis):

  * cvar-static     975  every call site of the registry insert FUN_141e9baa0 (1,013 of them, 982
                         static objects), hash read from obj+0x38 in the image
                         (tools\ghidra\HarvestCvarObjects.java / HarvestCvarNames.java); names
                         recovered by hashing every string in the exe against the hash set
  * command-builtin  37  the built-in console command table FUN_141277a50 (R1 §1.3.4, R5 §1.3)
  * alias-builtin     9  FUN_141e9b980 call sites inside FUN_141277a50
                         (tools\ghidra\HarvestCallStrings.java)
  * command-runtime   4  alias, clearcvar, dumpparticles, dumpeffects (FUN_141e9bd00 callers)

Names are NOT trusted blindly.  A static CVar's name is a pre-image recovered by hashing every
string in the exe, so a hash with two pre-images gets whichever one the consolidator kept: row
0xc9bbb38e reads "(" because the exe holds both "SceneUpdateList" and the junk string "u+|(", which
genuinely collide.  Every name is therefore re-hashed here with the client's own function
(CommandHash / FUN_14097fb10); a name that does not reproduce its row's hash is repaired from the
sidecar cvar-registry-preimages.tsv (identifier-shaped candidate first) and dropped to null if
nothing verifies.  The hash is the load-bearing value - a null name only costs a nicer error
message.

Usage:  python tools/devconsole/gen-client-registry.py [client-registry-1148.tsv] [output.g.cs]
"""
import re
import sys
from pathlib import Path

DEFAULT_TSV = Path(r"C:\Aug2017\out\devconsole-20260901\ghidra\client-registry-1148.tsv")
DEFAULT_OUT = (Path(__file__).resolve().parents[2]
               / "src" / "Cranberry.Zone" / "DevConsole" / "ClientRegistry1148.g.cs")

# FUN_14097fb10, byte for byte: fold table DAT_143cd0290 (ASCII to-upper), signed char into the
# sum, arithmetic (sar) shifts.  Kept here so the generator can verify the TSV without importing
# anything - it is the same algorithm CommandHash.cs implements and CommandHashTests pins.
def command_hash(name: str) -> int:
    h = 0
    for byte in name.encode("utf-8"):
        c = byte - 0x20 if 0x61 <= byte <= 0x7A else byte
        if c >= 0x80:
            c -= 0x100                      # movsx: bytes 0x80-0xff enter the sum negative
        h = ((h + c) * 0x401) & 0xFFFFFFFF
        signed = h if h < 0x80000000 else h - 0x100000000
        h = (h ^ ((signed >> 6) & 0xFFFFFFFF)) & 0xFFFFFFFF
    h = (h * 9) & 0xFFFFFFFF
    signed = h if h < 0x80000000 else h - 0x100000000
    h = (h ^ ((signed >> 11) & 0xFFFFFFFF)) & 0xFFFFFFFF
    return (h * 0x8001) & 0xFFFFFFFF


IDENTIFIER = re.compile(r"^[A-Za-z_][A-Za-z0-9_.]*$")


def read_preimages(path: Path):
    """hash -> the exe strings that hash to it (sidecar of the census; missing file is fine)."""
    table = {}
    if not path.is_file():
        return table
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        if not line or line.startswith("#"):
            continue
        fields = line.split("\t")
        if len(fields) < 3:
            continue
        table[int(fields[0], 16)] = [part.strip() for part in fields[2].split(" | ")]
    return table


def resolve_name(raw: str, expected: int, preimages):
    """The row's name if it hashes back to the row's hash, a verifying pre-image if one does, else None."""
    if raw and raw != "?" and command_hash(raw) == expected:
        return raw, "verified"

    candidates = [c for c in preimages.get(expected, []) if c and command_hash(c) == expected]
    # Two exe strings can share a hash (0xc9bbb38e is "SceneUpdateList" and "u+|("); the lookup
    # matches by hash either way, so prefer the one shaped like a CVar name for the error message.
    candidates.sort(key=lambda c: (IDENTIFIER.match(c) is None, -len(c)))
    if candidates:
        return candidates[0], "repaired"

    return None, "unnamed" if not raw or raw == "?" else "unverified"


def main() -> int:
    tsv = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_TSV
    out = Path(sys.argv[2]) if len(sys.argv) > 2 else DEFAULT_OUT

    preimages = read_preimages(tsv.with_name("cvar-registry-preimages.tsv"))

    rows, seen = [], {}
    counts = {"verified": 0, "repaired": 0, "unnamed": 0, "unverified": 0}
    for line in tsv.read_text(encoding="utf-8").splitlines():
        if not line or line.startswith("#"):
            continue
        fields = line.split("\t")
        if len(fields) < 3:
            continue
        value = int(fields[0], 16)
        name, how = resolve_name(fields[1].strip(), value, preimages)
        counts[how] += 1
        if value in seen:
            print(f"duplicate hash 0x{value:08x}: {seen[value]!r} and {name!r}", file=sys.stderr)
            continue
        seen[value] = name
        rows.append((value, name, fields[2].strip()))

    rows.sort(key=lambda row: row[0])
    kinds = {}
    for _, _, kind in rows:
        kinds[kind] = kinds.get(kind, 0) + 1
    kind_summary = ", ".join(f"{n} {k}" for k, n in sorted(kinds.items()))

    lines = [
        "// <auto-generated>",
        "// Generated by tools/devconsole/gen-client-registry.py from",
        f"// {tsv}",
        "// (harvested 2026-09-02 by tools/ghidra/HarvestCvarObjects.java, HarvestCvarNames.java and",
        "// HarvestCallStrings.java against C:\\Aug2017\\Client\\H1Z1.exe 0.0.118.208059, read-only).",
        f"// {len(rows)} entries: {kind_summary}.",
        "// Do not edit.",
        "// </auto-generated>",
        "",
        "// A file the compiler treats as generated opts out of the project's nullable context,",
        "// so the nullable Name below needs it back explicitly (CS8669).",
        "#nullable enable",
        "",
        "using System.Collections.Frozen;",
        "",
        "namespace Cranberry.Zone.DevConsole;",
        "",
        "/// <summary>One entry of the client's console registry: the hash is what the lookup matches on.</summary>",
        "/// <param name=\"Hash\"><see cref=\"CommandHash\"/> of the name, as the client stores it.</param>",
        "/// <param name=\"Name\">The name, when a pre-image for the hash is known; null otherwise.</param>",
        "/// <param name=\"Kind\">cvar-static, command-builtin, alias-builtin or command-runtime.</param>",
        "public sealed record ClientRegistryEntry(uint Hash, string? Name, string Kind);",
        "",
        "/// <summary>",
        "/// The August client's whole console CVar/command registry, by hash - the oracle that decides",
        "/// whether a name Cranberry pushes with <see cref=\"AddWorldCommand\"/> will be refused.",
        "/// <para>",
        "/// The client's lookup <c>FUN_141e9d680</c> walks the bucket <c>DAT_1452ebef0[hash and 0xff]</c> to",
        "/// the entry whose <c>+0x10</c> equals the hash and, when that entry's name pointer <c>+0x28</c> is",
        "/// null (every static CVar in the image), returns it <b>without comparing the name</b>. Membership",
        "/// is therefore decided by hash alone, which is why this type is a hash set and not a name set.",
        "/// </para>",
        "/// <para>",
        "/// A name in here can never be typed against Cranberry: <c>FUN_141280280</c> logs",
        "/// <c>Server sent %s (%s) that conflicts with a local command</c> to",
        "/// <c>Client\\Logs\\AdminCommands.log</c> and registers nothing, so the client emits no packet for",
        "/// it. Names added later at runtime (the client's own <c>alias</c> command, or another server's",
        "/// burst) are outside this census - the <c>AdminCommands.log</c> reader is the runtime backstop.",
        "/// </para>",
        "/// </summary>",
        "public static class ClientRegistry1148",
        "{",
        "    /// <summary>Every entry the client registers at start-up, ordered by hash.</summary>",
        "    public static readonly IReadOnlyList<ClientRegistryEntry> Entries =",
        "    [",
    ]

    for value, name, kind in rows:
        literal = "null" if name is None else "\"" + name.replace("\\", "\\\\").replace("\"", "\\\"") + "\""
        lines.append(f"        new(0x{value:08X}u, {literal}, \"{kind}\"),")

    lines += [
        "    ];",
        "",
        "    private static readonly FrozenDictionary<uint, ClientRegistryEntry> ByHash =",
        "        Entries.ToFrozenDictionary(entry => entry.Hash);",
        "",
        "    /// <summary>The hashes themselves - the set <c>CommandRegistry.Register</c> tests against.</summary>",
        "    public static FrozenSet<uint> Hashes { get; } = Entries.Select(entry => entry.Hash).ToFrozenSet();",
        "",
        "    /// <summary>True when the client already owns this hash, and a name that hashes to it is refused.</summary>",
        "    public static bool Contains(uint hash) => Hashes.Contains(hash);",
        "",
        "    /// <summary>",
        "    /// The entry behind a hash, for an error message: <c>\"god (alias-builtin)\"</c>, or",
        "    /// <c>\"0x... (cvar-static, name unknown)\"</c> when no pre-image was recovered, or",
        "    /// <c>\"0x... (not registered by the client)\"</c> when the client does not own it.",
        "    /// </summary>",
        "    public static string Describe(uint hash) =>",
        "        !ByHash.TryGetValue(hash, out ClientRegistryEntry? entry)",
        "            ? $\"0x{hash:x8} (not registered by the client)\"",
        "            : entry.Name is null",
        "                ? $\"0x{hash:x8} ({entry.Kind}, name unknown)\"",
        "                : $\"{entry.Name} ({entry.Kind})\";",
        "}",
        "",
    ]

    out.write_text("\n".join(lines), encoding="utf-8")
    print(f"{out}: {len(rows)} entries ({kind_summary})")
    print("  names: " + ", ".join(f"{n} {k}" for k, n in sorted(counts.items()) if n))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
