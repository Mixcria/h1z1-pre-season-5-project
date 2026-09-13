"""Separate exact numeric values from inferred cross-build column correspondence."""
import json
import pathlib
import struct
import sys

sys.dont_write_bytecode = True
from capture_source import load_capture, REPORTS, sha
from audit_capture import definitions
from weapon_1315 import parse

# Normalized record offsets: remove the two counted array contents, retain zero
# counts. These mappings have broad keyed historical signature support; none is
# a recovered 1315 native registration. Do not silently promote them to fact.
COLUMNS = (("AMMO_ITEM_ID", 13, "I"), ("REFIRE_TIME_MS", 25, "H"),
           ("RELOAD_TIME_MS", 38, "H"), ("RELOAD_CHAMBER_TIME_MS", 40, "H"),
           ("RELOAD_AMMO_FILL_TIME_MS", 42, "H"), ("RELOAD_LOOP_START_TIME_MS", 44, "H"),
           ("RELOAD_LOOP_END_TIME_MS", 46, "H"), ("COF_RECOIL", 54, "f"),
           ("COF_SCALAR", 58, "f"), ("COF_SCALAR_MOVING", 62, "f"),
           ("PLAYER_STATE_GROUP_ID", 234, "I"), ("ANIM_KICK_MAGNITUDE", 322, "f"),
           ("ANIM_RECOIL_MAGNITUDE", 326, "f"), ("SWAY_CROUCH_SCALAR", 400, "f"),
           ("FP_CAMERA_FOV", 474, "f"), ("AIM_ASSIST_CONFIG", 619, "I"))
RAW_RECOIL = ((74, "f"), (78, "f"), (82, "B"), (83, "H"), (85, "f"), (89, "f"),
              (93, "f"), (97, "f"), (101, "f"), (105, "B"), (106, "f"), (110, "f"),
              (114, "f"), (118, "f"), (122, "f"), (126, "f"), (130, "f"), (134, "f"),
              (138, "f"), (142, "f"), (146, "H"), (148, "f"), (152, "f"))


def normalized(body, mode):
    at = mode["offset"]
    return body[at:at+173] + bytes(4) + mode["fixedMiddle"] + bytes(4) + mode["fixedSuffix"]


def read(data, offset, kind):
    return struct.unpack_from("<" + kind, data, offset)[0]


def absolute(mode, normalized_offset):
    extra = (mode["array0"]["count"] * 9 if normalized_offset >= 177 else 0)
    extra += (mode["array1"]["count"] * 9 if normalized_offset >= 222 else 0)
    return mode["offset"] + normalized_offset + extra


def main():
    au, transport, decoded = load_capture()
    provenance, current = next((p, b) for p, b in definitions(decoded, au.TABLE.lz4_block_decompress)
                               if p["name"] == "WeaponDefinitions")
    previous = pathlib.Path("C:/Aug2017/out/rotk-shooting-20260911/reference/weapon-table-decompressed.bin").read_bytes()
    cp, pp = parse(current), parse(previous)
    cm, pm = ({r["id"]: r for r in p["lists"][2]} for p in (cp, pp))
    cn, pn = ({mid: normalized(body, r) for mid, r in rows.items()}
              for body, rows in ((current, cm), (previous, pm)))
    w = au.TABLE
    control_path = pathlib.Path("C:/Z1/Server/Data/friendWeaponDefinitions.bin")
    control, header = w.unwrap(control_path.read_bytes())
    table, consumed, error = w.walk_1087(control, "C:/Z1/Server/Data/weaponDefinitionSchema.json")
    if error or consumed != len(control): raise ValueError("historical naming control does not consume")
    old = {r["ID"]: r["DATA"]["DATA"] for r in table["FIRE_MODE_DEFINITIONS"]}
    august_path = REPORTS.parent / "weapons/table-audit/prospective/effective.json"
    august = json.loads(august_path.read_text())
    support = []
    for name, at, kind in COLUMNS:
        pairs = [(mid, values[name]) for mid, values in old.items() if mid in cn]
        nondefault = [(mid, value) for mid, value in pairs if value not in (0, 1)]
        agrees = lambda mid, value: abs(read(pn[mid], at, kind) - value) < 0.0001
        support.append({"candidateColumn": name, "normalizedRecordOffset": at, "kind": kind,
                        "historicalNondefaultMatches": sum(agrees(*p) for p in nondefault),
                        "historicalNondefaultCompared": len(nondefault),
                        "historicalAllMatches": sum(agrees(*p) for p in pairs), "historicalAllCompared": len(pairs),
                        "confidence": "inferred cross-build correspondence; not current native semantic proof"})
    compared = []
    for ar in august["Weapons"]:
        wid = ar["Definition"]["WeaponDefinitionId"]
        if wid not in (6, 1405, 1384, 1385): continue
        cw = next(r for r in cp["lists"][0] if r["id"] == wid)
        pw = next(r for r in pp["lists"][0] if r["id"] == wid)
        cg = [r for r in cp["lists"][1] if r["id"] in cw["groups"]]
        am = [m for g in ar["Groups"] for m in g["Modes"]]
        row = {"weaponDefinitionId": wid, "augustDefinition": ar["Definition"],
               "currentRecordOffset": cw["offset"], "list0Correspondences": [], "modes": []}
        for name, off in (("EquipTimeMs",13), ("UnequipTimeMs",17), ("ToIronSightsTimeMs",33),
                          ("FromIronSightsTimeMs",37), ("AimInAnimationTimeMs",41),
                          ("AimOutAnimationTimeMs",45), ("SprintRecoveryTimeMs",49)):
            row["list0Correspondences"].append({"name": name, "currentBodyOffset": cw["offset"] + off,
                "priorBodyOffset": pw["offset"] + off,
                "current": read(current, cw["offset"]+off, "I"), "priorRotk": read(previous,pw["offset"]+off,"I"),
                "finalAugust": ar["Definition"].get(name), "confidence": "inferred August field-order correspondence"})
        # Locate both current audio-corresponding words relative to the checked
        # animation string, not an assumed fixed record width.
        name_length = read(current, cw["offset"] + 89, "I")
        tail = cw["offset"] + 93 + name_length
        prior_tail = pw["offset"] + 93 + read(previous, pw["offset"] + 89, "I")
        row["audioWordsUnresolved"] = [{"currentBodyOffset": tail + i * 4,
            "priorBodyOffset": prior_tail + i * 4,
            "augustFieldCorrespondence": "word_0xc4" if i == 5 else "word_0xc8",
            "current": read(current, tail+i*4,"I"), "priorRotk": read(previous,prior_tail+i*4,"I"),
            "confidence": "position only; audio object/bank semantics not recovered"} for i in (5, 6)]
        for group in cg:
            for index, mid in enumerate(group["modes"]):
                candidate = {"id": mid, "group": group["id"], "index": index,
                             "bodyOffset": cm[mid]["offset"], "bytes": cm[mid]["bytes"],
                             "flagsByte8": cn[mid][8], "ironSightsBitCorrespondence": bool(cn[mid][8] & 4),
                             "augustMode": am[index]["FireModeId"] if index < len(am) else None,
                             "augustComparisonBasis": "group order only; current active/alternate selection not proven",
                             "correspondences": [], "unnamedRecoilRegion": []}
                acols = {col["Name"]: (col["Value"], f["RecordOffset"], f["WireOffset"])
                         for f in am[index]["Fields"] for col in f["Columns"]} if index < len(am) else {}
                for name, off, kind in COLUMNS:
                    av = acols.get(name)
                    candidate["correspondences"].append({"candidateColumn": name, "currentBodyOffset": absolute(cm[mid],off),
                        "current": read(cn[mid],off,kind), "priorRotk": read(pn[mid],off,kind),
                        "finalAugust": av[0] if av else None, "augustRecordOffset": av[1] if av else None,
                        "augustWireOffset": av[2] if av else None})
                candidate["unnamedRecoilRegion"] = [{"recordOffset": off, "currentBodyOffset": cm[mid]["offset"]+off,
                    "primitive": kind, "current": read(cn[mid],off,kind), "priorRotk": read(pn[mid],off,kind)} for off,kind in RAW_RECOIL]
                candidate["finalAugustNamedRecoil"] = {name: value[0] for name,value in acols.items() if name.startswith("RECOIL_")}
                row["modes"].append(candidate)
        compared.append(row)
    semantic_joins = {}
    for label, off, list_number in (("candidatePlayerStateGroup",234,3), ("candidateAimAssist",619,5)):
        ids = {r["id"] for r in cp["lists"][list_number]}
        semantic_joins[label] = [{"mode": mid, "candidateId": read(data,off,"I"), "bodyOffset": absolute(cm[mid],off)}
                                for mid,data in cn.items() if read(data,off,"I") and read(data,off,"I") not in ids]
    result = {"current": provenance, "priorRotkSha256": sha(previous),
              "historicalNamingControl": {"protocol":1087,"bodyBytes":len(control),"bodySha256":sha(control)},
              "finalAugust": {"path":str(august_path),"auditSha256":sha(august_path.read_bytes()),
                              "bodySha256":august["FinalSha256"],"configSha256":august["ConfigSha256"],"context":august["Context"]},
              "columnSupport": support, "weapons": compared, "unresolvedSemanticJoins":semantic_joins,
              "decision": "No current-1315 recoil/recovery/first-shot/audio/optic value is promoted to verified August tuning. Strong numeric correspondences remain inferred until current native field/consumer evidence resolves naming and alternate-mode selection.",
              "recoilLimit": "The bytes near97/101/118 resemble historical magnitude/first-shot order, but current recovery/angle/array insertions and low distinct-value signature agreement do not validate those names. Raw typed scalars and final August named fields are intentionally separate.",
              "opticLimit": "FP_CAMERA_FOV has300/300 historical signature agreement but only5 nondefault modes. This does not prove active binoc zoom stage selection or current camera restrictions."}
    (REPORTS / "weapon-field-comparison.json").write_text(json.dumps(result,indent=2)+"\n",encoding="utf-8")
    print(json.dumps({"columnsCompared":len(support),"weapons":len(compared),"unresolvedCandidateJoins":{k:len(v) for k,v in semantic_joins.items()}}))


if __name__ == "__main__": main()
