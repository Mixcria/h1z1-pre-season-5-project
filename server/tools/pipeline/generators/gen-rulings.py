#!/usr/bin/env python3
r"""
gen-rulings.py - turn ``rulings/*.json`` into ``src/Cranberry.Zone/Generated/Rulings.g.cs``.

WHY THIS EXISTS (docs/101, S8 §7.5 step 3, overhaul plan §3 lane 2C)

  Before this file the owner's rulings were C# literals. ``GasSettings.cs`` knew that the first
  ring reveals at 120 s; nothing in the tree knew *who decided that*, *where the number came
  from*, or *whether the client could have supplied it*. ``tools/pipeline/manifest.json``'s
  ``RULING`` column was empty for exactly that reason (docs/96 §5): a value that lives only in a
  hand-written ``.cs`` file is not data, so the pipeline could not grade it.

  ``rulings/<subsystem>.json`` is that data. One row per number:

      { "key", "value", "ruling": "Dxx" | "none", "source", "grade", "note", "csharp"? }

  * ``ruling`` names the ``docs/01-decisions.md`` row that decided it, or ``"none"`` - in which
    case the row is a self-declared UNRULED value and docs/101 lists it for the owner.
  * ``source`` is where the value physically came from: a ``Z1 <file>:<line>`` citation under
    D53, a ``docs/NN §x`` derivation, or a client sheet column.
  * ``grade`` is the same six-word vocabulary as ``pipeline.py``: CLIENT, CLIENT+RULING, RULING,
    Z1, CAPTURED, THIRD_PARTY_SHAPED.
  * ``csharp`` is optional. A row that carries it gets a generated constant and the value class
    stops holding a literal; a row without it is *documentation only* - either because the value
    already ships inside another graded artefact (the loot roster lives in
    ``z2-loot-tables.json``) or because its home file belongs to another lane this wave.

  Emitted C# types: float, double, int, uint, long, ulong, bool, string, float3 (Vector3),
  and the array forms int[], uint[], float[], double[], string[]. Primitives become ``const``
  so a ``const`` in a value class can still reference them; everything else becomes
  ``static readonly``.

THE LOOT CROSS-CHECK

  ``rulings/loot-gates.json`` and ``rulings/loot-roster.json`` restate numbers that already ship
  inside ``src/Cranberry.Zone/Data/Loot/z2-loot-tables.json``. Two copies of a number with
  nothing to keep them equal is a defect, so this generator *verifies* every one of them against
  that file and fails rather than emitting a lie. That is why ``z2-loot-tables.json`` is one of
  this generator's declared inputs in ``pipeline.py``.

Usage (pipeline.py drives it; the argument is the staging directory):

    python tools/pipeline/generators/gen-rulings.py <out-dir>
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]                                   # C:\Aug2017\Server
RULINGS_DIR = REPO / "rulings"
LOOT_TABLES = REPO / "src" / "Cranberry.Zone" / "Data" / "Loot" / "z2-loot-tables.json"

#: the order the subsystems appear in the generated file (and in docs/101)
SUBSYSTEMS = [
    "movement",
    "gas",
    "descent",
    "sky",
    "loot-gates",
    "loot-roster",
    "vehicles-plan",
    "crafting",
    "starter",
    "throwables",
]

GRADES = {"CLIENT", "CLIENT+RULING", "RULING", "Z1", "CAPTURED", "THIRD_PARTY_SHAPED"}

#: combat value classes another lane owns - listed so docs/101's TODO and this file agree
COMBAT_TODO = ["retail-damage", "medical", "armour", "weapon-definitions", "firemodes"]


# ======================================================================================
# reading
# ======================================================================================


def load(subsystem: str) -> dict:
    path = RULINGS_DIR / f"{subsystem}.json"
    if not path.is_file():
        raise SystemExit(f"missing rulings file: {path}")
    doc = json.loads(path.read_text(encoding="utf-8"))
    if doc.get("schema") != "cranberry-rulings/1":
        raise SystemExit(f"{path.name}: unknown schema {doc.get('schema')!r}")
    if doc.get("subsystem") != subsystem:
        raise SystemExit(f"{path.name}: subsystem is {doc.get('subsystem')!r}")
    for row in doc["rows"]:
        for field in ("key", "ruling", "source", "grade"):
            if field not in row:
                raise SystemExit(f"{path.name}: row {row.get('key')!r} has no {field!r}")
        if row["grade"] not in GRADES:
            raise SystemExit(f"{path.name}: row {row['key']!r} has grade {row['grade']!r}")
        if row["ruling"] != "none" and not row["ruling"].startswith("D"):
            raise SystemExit(f"{path.name}: row {row['key']!r} ruling {row['ruling']!r} "
                             "is neither a Dxx nor \"none\"")
        if row["ruling"] == "none" and not row.get("note"):
            raise SystemExit(f"{path.name}: row {row['key']!r} is unruled and carries no note")
    keys = [r["key"] for r in doc["rows"]]
    if len(set(keys)) != len(keys):
        raise SystemExit(f"{path.name}: duplicate key")
    names = [r["csharp"]["name"] for r in doc["rows"] if r.get("csharp")]
    if len(set(names)) != len(names):
        raise SystemExit(f"{path.name}: duplicate C# constant name")
    return doc


# ======================================================================================
# the loot cross-check
# ======================================================================================


def value_of(doc: dict, key: str):
    for row in doc["rows"]:
        if row["key"] == key:
            return row["value"]
    raise SystemExit(f"{doc['subsystem']}.json: expected a row for {key!r}")


def check_loot(gates: dict, roster: dict) -> list[str]:
    """Every number these two files restate must equal z2-loot-tables.json's own."""
    if not LOOT_TABLES.is_file():
        raise SystemExit(f"missing {LOOT_TABLES}")
    tables = json.loads(LOOT_TABLES.read_text(encoding="utf-8"))
    categories = {c["key"]: c for c in tables["categories"]}
    density = tables["density"]
    clusters = tables["clusters"]
    checked: list[str] = []

    def same(what: str, mine, theirs) -> None:
        if isinstance(mine, float) or isinstance(theirs, float):
            ok = abs(float(mine) - float(theirs)) <= 1e-9
        else:
            ok = mine == theirs
        if not ok:
            raise SystemExit(
                f"rulings drift: {what} is {mine!r} in rulings/ and {theirs!r} in "
                f"{LOOT_TABLES.name}. Fix the ruling row or regenerate the table - do not "
                f"let the two disagree.")
        checked.append(what)

    for family in ("Weapons01", "Gear01", "Backpack01", "FirstAidKit01", "Ammo01"):
        same(f"LootDensityOptions.{family}SpawnChance",
             value_of(gates, f"LootDensityOptions.{family}SpawnChance"),
             categories[family]["spawnChance"])
        same(f"Roster.{family}.entryCount",
             value_of(roster, f"Roster.{family}.entryCount"),
             len(categories[family]["entries"]))
        same(f"Roster.{family}.totalWeight",
             value_of(roster, f"Roster.{family}.totalWeight"),
             sum(e["weight"] for e in categories[family]["entries"]))

    same("LootDensityOptions.RoomRadiusMetres",
         value_of(gates, "LootDensityOptions.RoomRadiusMetres"), density["roomRadiusMetres"])
    same("LootDensityOptions.RoomHeightMetres",
         value_of(gates, "LootDensityOptions.RoomHeightMetres"), density["roomHeightMetres"])
    same("LootDensityOptions.MaxItemsPerRoom",
         value_of(gates, "LootDensityOptions.MaxItemsPerRoom"), density["maxItemsPerRoom"])
    same("LootDensityOptions.MaxWeaponsPerRoom",
         value_of(gates, "LootDensityOptions.MaxWeaponsPerRoom"), density["maxWeaponsPerRoom"])
    same("LootDensityOptions.SingletonKinds",
         value_of(gates, "LootDensityOptions.SingletonKinds"), density["singletonKinds"])

    same("Cluster.OffsetMetres",
         value_of(roster, "Cluster.OffsetMetres"), clusters["offsetMetres"])
    same("Cluster.SecondBoxYawRadians",
         value_of(roster, "Cluster.SecondBoxYawRadians"), clusters["secondBoxYawOffsetRadians"])
    same("Cluster.boxCount", value_of(roster, "Cluster.boxCount"), len(clusters["boxes"]))
    same("Cluster.roundsPerBox",
         value_of(roster, "Cluster.roundsPerBox"), [b["count"] for b in clusters["boxes"]])

    ids = {e["itemDefinitionId"] for c in tables["categories"] for e in c["entries"]}
    same("Roster.tableItemIdCount", value_of(roster, "Roster.tableItemIdCount"), len(ids))
    return checked


# ======================================================================================
# emitting
# ======================================================================================


def _real(value) -> str:
    """Python's shortest round-tripping form, always with a decimal point so C# reads a real."""
    text = repr(float(value))
    if "." not in text and "e" not in text and "E" not in text and "inf" not in text:
        text += ".0"
    return text


def cs_scalar(kind: str, value) -> str:
    if kind == "float":
        return _real(value) + "f"
    if kind == "double":
        return _real(value) + "d"
    if kind in ("int", "long"):
        return str(int(value))
    if kind == "uint":
        return f"{int(value)}u"
    if kind == "ulong":
        return f"{int(value)}UL"
    if kind == "bool":
        return "true" if value else "false"
    if kind == "string":
        return '"' + str(value).replace("\\", "\\\\").replace('"', '\\"') + '"'
    raise SystemExit(f"unknown scalar type {kind!r}")


def cs_declaration(name: str, kind: str, value) -> str:
    if kind == "float3":
        x, y, z = value
        return (f"    public static readonly Vector3 {name} = "
                f"new({_real(x)}f, {_real(y)}f, {_real(z)}f);")
    if kind.endswith("[]"):
        element = kind[:-2]
        items = ", ".join(cs_scalar(element, v) for v in value)
        return f"    public static readonly {element}[] {name} = [{items}];"
    return f"    public const {kind} {name} = {cs_scalar(kind, value)};"


def xml_escape(text: str) -> str:
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def wrap(text: str, width: int = 96) -> list[str]:
    words, line, out = text.split(), "", []
    for word in words:
        if line and len(line) + 1 + len(word) > width:
            out.append(line)
            line = word
        else:
            line = f"{line} {word}".strip()
    if line:
        out.append(line)
    return out or [""]


def class_name(subsystem: str, doc: dict) -> str:
    name = doc.get("class")
    if not name:
        raise SystemExit(f"{subsystem}.json has no \"class\"")
    return name


def emit(docs: dict[str, dict], checked: list[str]) -> str:
    lines: list[str] = []
    w = lines.append

    w("// Do not edit. Generated by tools/pipeline/generators/gen-rulings.py from rulings/*.json.")
    w("// The provenance header above is written by tools/pipeline/pipeline.py (docs/96).")
    w("//")
    w("// WHAT THIS FILE IS (docs/101, overhaul plan section 3 lane 2C)")
    w("//")
    w("//   Every number in the server that the August client does NOT supply, declared once,")
    w("//   with the docs/01 decision that chose it, the place it physically came from, and its")
    w("//   provenance grade. The value classes read these constants instead of holding literals,")
    w("//   so \"who decided this, and on what evidence\" is answerable from the tree rather than")
    w("//   from a comment.")
    w("//")
    w("//   Changing a number here changes the game. Change rulings/<subsystem>.json, re-run")
    w("//   `python tools/pipeline/pipeline.py generate --only rulings`, and expect")
    w("//   Cranberry.Tests.Zone.RulingsTests to fail until the pinned number in it is updated")
    w("//   too - that test exists so a value edit can never be silent.")
    w("//")
    w(f"//   Loot cross-check: {len(checked)} number(s) restated in rulings/loot-*.json were")
    w("//   verified against src/Cranberry.Zone/Data/Loot/z2-loot-tables.json when this file was")
    w("//   written, so the two cannot drift apart.")
    w("")
    w("using System.Numerics;")
    w("")
    w("namespace Cranberry.Zone.Generated;")
    w("")
    w("/// <summary>")
    w("/// The owner's rulings, as typed constants. One nested class per subsystem; see")
    w("/// <c>rulings/*.json</c> for the full row (ruling, source, grade, note) behind each one.")
    w("/// </summary>")
    w("public static class Rulings")
    w("{")

    first = True
    for subsystem in SUBSYSTEMS:
        doc = docs[subsystem]
        rows = [r for r in doc["rows"] if r.get("csharp")]
        if not first:
            w("")
        first = False

        w("    /// <summary>")
        for chunk in wrap(f"{subsystem} - {doc['summary'].splitlines()[0]}", 92):
            w(f"    /// {xml_escape(chunk)}")
        w("    /// <para>")
        w(f"    /// Source: <c>rulings/{subsystem}.json</c>. Rulings: "
          f"{', '.join(doc.get('rulings') or []) or '(none)'}.")
        w("    /// </para>")
        w("    /// </summary>")
        w(f"    public static class {class_name(subsystem, doc)}")
        w("    {")

        inner_first = True
        for row in rows:
            if not inner_first:
                w("")
            inner_first = False
            spec = row["csharp"]
            w("        /// <summary>")
            head = (f"{row['key']} = {json.dumps(row['value'])}. "
                    f"Ruling {row['ruling']}. Grade {row['grade']}.")
            for chunk in wrap(head, 88):
                w(f"        /// {xml_escape(chunk)}")
            w("        /// <para>")
            for chunk in wrap("Source: " + row["source"], 88):
                w(f"        /// {xml_escape(chunk)}")
            w("        /// </para>")
            if row.get("note"):
                w("        /// <para>")
                for chunk in wrap(row["note"], 88):
                    w(f"        /// {xml_escape(chunk)}")
                w("        /// </para>")
            w("        /// </summary>")
            w("    " + cs_declaration(spec["name"], spec["type"], row["value"]))
        w("    }")

    w("")
    w("    /// <summary>")
    w("    /// The value classes this lane did NOT move, and who owns them instead. Combat's")
    w("    /// numbers (the retail damage table, the medical and armour models and the weapon")
    w("    /// definition lists) are a different lane's rulings and have no file under")
    w("    /// <c>rulings/</c> yet; docs/101 carries the TODO.")
    w("    /// </summary>")
    w("    public static readonly string[] SubsystemsNotYetRuled =")
    w("        [" + ", ".join(f'"{name}"' for name in COMBAT_TODO) + "];")
    w("}")
    w("")
    return "\n".join(lines)


# ======================================================================================


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        sys.stderr.write(__doc__ or "")
        return 2
    out_dir = Path(argv[1])
    out_dir.mkdir(parents=True, exist_ok=True)

    docs = {name: load(name) for name in SUBSYSTEMS}
    checked = check_loot(docs["loot-gates"], docs["loot-roster"])

    text = emit(docs, checked)
    (out_dir / "Rulings.g.cs").write_text(text, encoding="utf-8", newline="\n")

    emitted = sum(1 for d in docs.values() for r in d["rows"] if r.get("csharp"))
    total = sum(len(d["rows"]) for d in docs.values())
    unruled = sum(1 for d in docs.values() for r in d["rows"] if r["ruling"] == "none")
    print(f"gen-rulings: {total} row(s) over {len(SUBSYSTEMS)} subsystem(s); "
          f"{emitted} constant(s) emitted, {total - emitted} documentation-only, "
          f"{unruled} unruled; {len(checked)} loot number(s) cross-checked")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
