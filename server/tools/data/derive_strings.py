#!/usr/bin/env python3
r"""derive_strings - every locale string the server names, resolved from the client's own files.

INPUT
    ``C:\Aug2017\out\data_aug\CodeStringMappings.txt``   1,844 rows ``MESSAGE_NAME^*STRING_ID``
        - the client's own name for a string id. This is the anchor for everything the client
          itself has a name for (``BR.Start``, ``BR.RemainingPlayers``, ``Match.WaitingForPlayers``).
    ``C:\Aug2017\out\data_aug\locale-en_us.json``        9,330 rows ``localeKey -> en_us text``,
        the parse of ``Client\Locale\en_us_data.dat`` by ``tools/locale/localedat.py dump``.

OUTPUT
    ``C:\Aug2017\out\data_aug\derived\strings.json``

WHY (S8 section 4.3 "Locale: ids hand-copied into C#", overhaul plan section 3 lane 2B)

    Four files in ``src`` carried locale ids as decimal literals with the English text in a
    comment beside them: ``Gas/GasAlerts.cs``, ``Match/MatchAlerts.cs``,
    ``World/Doors/InteractionStringPackets.cs`` and ``MatchFlowPackets.cs``. A literal cannot be
    checked, and the comment beside it cannot be checked either. Here the id is **resolved** and
    the English text is **read**, both out of the August client, and the generator writes them
    together so the doc comment can never drift from the constant.

HOW AN ID IS RESOLVED - two anchors, never a typed number

  S1  ``codeName``. The client's own ``CodeStringMappings.txt`` maps a message name to the
      string id. Every alert and every HUD countdown label the server sends has one, so the
      request names ``BR.Start`` and the id falls out of the sheet. If the client renames or
      renumbers the row, this deriver moves with it.
  S2  ``text`` + ``occurrence``. The world-space interaction prompts (``09 2d``) have **no**
      ``CodeStringMappings`` row - docs/47 section 4e found them by inverting the locale key
      instead. That inversion is reproduced here: ``localedat.text_key(n) =
      jenkins_lookup2("Global.Text." + str(n))`` is evaluated for every ``n`` in
      ``0..SCAN_LIMIT``, 7,463 of which land on a real locale key, and the request names the
      exact en_us TEXT it wants. Three of the eight prompt texts are carried by two ids each
      ("[[*key*]] Open [*target*]" is both 31 and 12156), so a request may also name the
      1-based ``occurrence`` among the matching ids in ascending order. **Which id to use for
      which object is Cranberry's choice** (docs/47 says so at the constant); what is derived
      is that the id carries that text on this build.

    Every row is written back with BOTH facts - the resolved id and the text it resolves to -
    so ``AugustStrings.g.cs`` can put the English in the doc comment and a test can pin the id.

DETERMINISM
    No clock (docs/96 section 8): the identity of the document is the two inputs' sha256.

USAGE
    python tools/data/derive_strings.py --out C:\Aug2017\out\data_aug\derived\strings.json
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[1].parent
sys.path.insert(0, str(REPO / "tools" / "pipeline"))
sys.path.insert(0, str(REPO / "tools" / "locale"))

from localedat import text_key  # noqa: E402
from readers.sheet import read_rows  # noqa: E402

SCHEMA = "cranberry.derived.strings/1"
CLIENT_BUILD = "0.0.118.208059"

DEFAULT_DATA = Path(r"C:\Aug2017\out\data_aug")
DEFAULT_OUT = Path(r"C:\Aug2017\out\data_aug\derived\strings.json")

#: How far the S2 inversion scans. Datasheet string ids are small (the largest this build uses
#: is 18,811,314 in a description column, but every *UI* id is well under 20,000); 65,536 covers
#: every id any sheet in this build puts in a NAME_ID/STRING_ID column and costs 0.1 s.
SCAN_LIMIT = 65_536

#: ---------------------------------------------------------------------------------------
#: THE REQUEST. One row per string the server names, keyed by the CLIENT's own vocabulary.
#:
#:   member      the C# constant name AugustStrings.g.cs emits
#:   group       which nested class it lands in
#:   codeName    S1 anchor - a CodeStringMappings.txt MESSAGE_NAME
#:   text        S2 anchor - the exact en_us text, for the prompts that have no code name
#:   occurrence  1-based, among ids carrying that text in ascending order (S2 only)
#:   consumer    the file that used to hold this id as a literal - the migration's own record
#:   note        why the server sends it
#: ---------------------------------------------------------------------------------------
REQUESTS: list[dict] = [
    # -- ClientUpdate.TextAlert (11 31), the banner channel -------------------------------
    {"member": "MatchBegun", "group": "Alerts", "codeName": "BR.Start",
     "consumer": "Gas/GasAlerts.cs", "note": "broadcast when the match opens"},
    {"member": "Proceed", "group": "Alerts", "codeName": "BR.Proceed",
     "consumer": "Gas/GasAlerts.cs", "note": "broadcast with the first safe-zone reveal"},
    {"member": "ReleasingGas", "group": "Alerts", "codeName": "BR.ReleasingGas",
     "consumer": "Gas/GasAlerts.cs", "note": "broadcast when a ring starts moving"},
    {"member": "SafeZoneMarked", "group": "Alerts", "codeName": "BR.SafeZoneAnnounce",
     "consumer": "Gas/GasAlerts.cs",
     "note": "carries #count([*slot0*]); the slot is expanded server-side because TextAlert "
             "ships a string, not an id"},
    {"member": "Remaining", "group": "Alerts", "codeName": "BR.RemainingPlayers",
     "consumer": "Match/MatchAlerts.cs", "note": "broadcast on every elimination (S4 row E7)"},
    {"member": "WinnerAnnounced", "group": "Alerts", "codeName": "BR.AnnounceWinner",
     "consumer": "Match/MatchAlerts.cs", "note": "broadcast to everyone when the match is won"},
    {"member": "CelebrationEnding", "group": "Alerts", "codeName": "BR.EndingWinnerCelebration",
     "consumer": "Match/MatchAlerts.cs", "note": "the clock on the winner's celebration"},
    {"member": "Connected", "group": "Alerts", "codeName": "BR.PlayersConnected",
     "consumer": "Match/MatchAlerts.cs", "note": "the lobby's population line"},

    # -- GameMode.Countdown (ce 0f) label ids ---------------------------------------------
    {"member": "RevealingSafeZone", "group": "HudLabels", "codeName": "BR.RevealingSafeZone",
     "consumer": "MatchFlowPackets.cs (GameModeHud)",
     "note": "the countdown label while a circle has not been revealed yet"},
    {"member": "GasAdvancesIn", "group": "HudLabels", "codeName": "BR.GasAdvancesIn",
     "consumer": "MatchFlowPackets.cs (GameModeHud)",
     "note": "a circle is drawn and its ring is still holding"},
    {"member": "GasIsSpreading", "group": "HudLabels", "codeName": "BR.GasIsSpreading",
     "consumer": "MatchFlowPackets.cs (GameModeHud)", "note": "the ring is travelling"},
    {"member": "StartingMatch", "group": "HudLabels", "codeName": "SyncTeleport.StartingMatch",
     "consumer": "MatchFlowPackets.cs (GameModeHud)", "note": "the lobby's own countdown label"},
    {"member": "WaitingForPlayers", "group": "HudLabels", "codeName": "Match.WaitingForPlayers",
     "consumer": "(none yet - the lobby table of overhaul plan lane 1C)",
     "note": "shown below the minimum population; declared here so lane 1C never types it"},

    # -- Command.InteractionString (09 2d) world prompts ----------------------------------
    #    No CodeStringMappings row exists for any of these; the text is the anchor (S2).
    {"member": "PickUpTarget", "group": "Prompts", "text": "[[*key*]] Pick Up [*target*]",
     "consumer": "World/Doors/InteractionStringPackets.cs", "note": "ground loot"},
    {"member": "Open", "group": "Prompts", "text": "[[*key*]] Open",
     "consumer": "World/Doors/InteractionStringPackets.cs",
     "note": "a closed door - no [*target*] token, which is why a door (NAME_ID 0) can use it"},
    {"member": "CloseDoor", "group": "Prompts", "text": "[[*key*]] Close Door",
     "consumer": "World/Doors/InteractionStringPackets.cs", "note": "an open door"},
    {"member": "UseDoor", "group": "Prompts", "text": "<[[*key*]] Use Door>",
     "consumer": "World/Doors/InteractionStringPackets.cs", "note": "the alternative door wording"},
    {"member": "OpenTarget", "group": "Prompts", "text": "[[*key*]] Open [*target*]",
     "occurrence": 2,
     "consumer": "World/Doors/InteractionStringPackets.cs",
     "note": "needs a NAME_ID on the target. Two ids carry this text (31 = AccessTarget and "
             "12156); Cranberry has always used the second (docs/47 section 7 q4)"},
    {"member": "UseGate", "group": "Prompts", "text": "[[*key*]] Use Gate",
     "consumer": "World/Doors/InteractionStringPackets.cs", "note": "a gate"},
    {"member": "SearchTarget", "group": "Prompts", "text": "[[*key*]] Search [*target*]",
     "occurrence": 2,
     "consumer": "World/Doors/InteractionStringPackets.cs",
     "note": "containers, later. Two ids carry this text (1191 and 1326); Cranberry uses the second"},
    {"member": "TakeTarget", "group": "Prompts", "text": "[[*key*]] Take [*target*]",
     "occurrence": 1,
     "consumer": "World/Doors/InteractionStringPackets.cs",
     "note": "two ids carry this text (29 = the client's own PickUpTarget row, and 9961); "
             "Cranberry uses the first"},
]

PROVENANCE = {
    "strings[].id": "CodeStringMappings.txt.*STRING_ID (S1) | inverted locale key (S2)",
    "strings[].codeName": "CodeStringMappings.txt.MESSAGE_NAME",
    "strings[].localeKey": "jenkins_lookup2(\"Global.Text.\" + id) - tools/locale/localedat.py",
    "strings[].text": "Client/Locale/en_us_data.dat via out/data_aug/locale-en_us.json",
}

SERVER_SIDE_GAPS = [
    "TextAlert (11 31) carries a STRING, not a string id, so the server expands the client's "
    "#count([*slot0*]) / [*slot0*] tokens itself. The expansion rule is Cranberry's; the "
    "template is the client's.",
    "Which prompt id belongs on which object is Cranberry's choice, not a client fact "
    "(docs/47: '[P-data] - which text a given id carries is proven; which id to use for which "
    "object is Cranberry's choice').",
    "Only en_us is resolved. The other eight archives exist and the same key works in all of "
    "them; nothing on the wire is localised yet.",
]


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_code_names(path: Path) -> dict[str, int]:
    out: dict[str, int] = {}
    for row in read_rows(path):
        name = row.get("MESSAGE_NAME", "")
        raw = row.get("STRING_ID", "")
        if not name or not raw.isdigit():
            continue
        out.setdefault(name, int(raw))
    return out


def load_locale(path: Path) -> dict[int, str]:
    raw = json.loads(path.read_text(encoding="utf-8"))
    return {int(key): value for key, value in raw.items()}


def invert(locale: dict[int, str], limit: int) -> dict[str, list[int]]:
    """S2: every datasheet string id in ``0..limit`` that resolves, grouped by its exact text."""
    by_text: dict[str, list[int]] = {}
    for candidate in range(limit):
        text = locale.get(text_key(candidate))
        if text is not None:
            by_text.setdefault(text, []).append(candidate)
    return by_text


def derive(data_dir: Path, limit: int) -> dict:
    mappings_path = data_dir / "CodeStringMappings.txt"
    locale_path = data_dir / "locale-en_us.json"

    code_names = load_code_names(mappings_path)
    locale = load_locale(locale_path)
    by_text = invert(locale, limit)

    rows: list[dict] = []
    unresolved: list[str] = []
    for request in REQUESTS:
        member = request["member"]
        code_name = request.get("codeName")
        wanted_text = request.get("text")

        if code_name is not None:
            string_id = code_names.get(code_name)
            if string_id is None:
                unresolved.append(f"{member}: CodeStringMappings.txt has no row {code_name!r}")
                continue
            anchor = "codeName"
            candidates = [string_id]
        elif wanted_text is not None:
            candidates = by_text.get(wanted_text, [])
            occurrence = int(request.get("occurrence", 1))
            if len(candidates) < occurrence:
                unresolved.append(
                    f"{member}: en_us has {len(candidates)} id(s) carrying {wanted_text!r}, "
                    f"occurrence {occurrence} requested")
                continue
            string_id = candidates[occurrence - 1]
            anchor = "text"
        else:
            raise SystemExit(f"{member}: a request needs either codeName or text")

        key = text_key(string_id)
        text = locale.get(key)
        if text is None:
            unresolved.append(
                f"{member}: string id {string_id} hashes to locale key {key}, which en_us "
                "does not carry")
            continue

        rows.append({
            "member": member,
            "group": request["group"],
            "id": string_id,
            "codeName": code_name,
            "codeNamesForId": sorted(n for n, i in code_names.items() if i == string_id),
            "localeKey": key,
            "text": text,
            "anchor": anchor,
            "occurrence": int(request.get("occurrence", 1)) if anchor == "text" else None,
            "idsCarryingThisText": candidates if anchor == "text" else None,
            "consumer": request["consumer"],
            "note": request["note"],
        })

    if unresolved:
        for line in unresolved:
            sys.stderr.write(f"[derive_strings] UNRESOLVED {line}\n")
        raise SystemExit(f"{len(unresolved)} string request(s) did not resolve")

    members = [row["member"] for row in rows]
    if len(set(members)) != len(members):
        raise SystemExit("duplicate member name in REQUESTS")

    return {
        "schema": SCHEMA,
        "clientBuild": CLIENT_BUILD,
        "note": "Every locale string the server names, resolved from the client's own "
                "CodeStringMappings.txt and en_us archive. No clock: the identity of this "
                "document is sources[].sha256 (docs/96 section 8).",
        "sources": [
            {"path": "out/data_aug/CodeStringMappings.txt", "sha256": sha256_of(mappings_path),
             "size": mappings_path.stat().st_size, "grade": "CLIENT",
             "rows": len(code_names)},
            {"path": "out/data_aug/locale-en_us.json", "sha256": sha256_of(locale_path),
             "size": locale_path.stat().st_size, "grade": "CLIENT",
             "rows": len(locale)},
        ],
        "counts": {
            "requested": len(REQUESTS),
            "resolved": len(rows),
            "byCodeName": sum(1 for row in rows if row["anchor"] == "codeName"),
            "byText": sum(1 for row in rows if row["anchor"] == "text"),
            "scanLimit": limit,
            "idsResolvingInScanRange": sum(len(v) for v in by_text.values()),
            "localeRecords": len(locale),
            "codeStringMappings": len(code_names),
        },
        "provenance": PROVENANCE,
        "serverSideGaps": SERVER_SIDE_GAPS,
        "strings": rows,
    }


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--data", type=Path, default=DEFAULT_DATA)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT)
    parser.add_argument("--scan-limit", type=int, default=SCAN_LIMIT)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    document = derive(args.data, args.scan_limit)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(
        json.dumps(document, indent=2, ensure_ascii=False, sort_keys=False) + "\n",
        encoding="utf-8", newline="\n")
    counts = document["counts"]
    print(f"[derive_strings] {counts['resolved']}/{counts['requested']} strings resolved "
          f"({counts['byCodeName']} by code name, {counts['byText']} by text) -> {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
