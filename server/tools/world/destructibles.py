"""Generate August Z2 shooting breakables from its own placements and effect table.

Health is server tuning (2000 glass / 5000 wood under D329, 1000 barrels,
4000 authored wall blockers), not a claim that the client publishes retail health.
"""
import hashlib
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
AUG = ROOT.parent
FAMILIES = {
    "City_Structures_Buildings_Int_WallHoleBlocked01": "PFX_Damage_Building_Interior_WallHole01",
    "Common_Props_IndustrialElements_RedBarrel01": "PFX_Explosion_OilBarrel_04m",
    "Common_Props_GlassWindow01": "GlassWindow_House",
    "Common_Props_TintedWindow01": "GlassWindow_House",
    "Farm_Props_Fences_Fence01": "Fence_Farm01",
    "Farm_Props_Fences_Fence02": "Fence_Farm02",
    "Farm_Props_Fences_Post01": "Fence_Farm01_Post",
    "Farm_Props_Fences_FenceGate01": "Fence_Farm01",
    "Residential_Props_Fence_Tall01_Side01": "Fence_ResidTall01",
    "Residential_Props_Fence_Tall01_Side02": "Fence_ResidTall02",
    "Residential_Props_Fence_Tall01_Post01": "Fence_ResidTall01",
    "Residential_Props_Fence_Tall01_Gate01": "Fence_ResidTallGate",
    "Farm_Props_CorralFence_Fence01": "Corral_Fence_01",
    "Farm_Props_CorralFence_FenceBroken01": "Corral_FenceBroken_01",
    "Farm_Props_CorralFence_Gate01": "Corral_Gate_01",
    "Farm_Props_CorralFence_GateDoor01": "Corral_GateDoor_01",
}


def health_for(model):
    if model == "City_Structures_Buildings_Int_WallHoleBlocked01":
        # Declared parity tuning: a close shotgun blast can remove the authored panel.
        # The 2026-09-12 footage does not establish its initial health or pellet count.
        return 4000
    return 1000 if "RedBarrel" in model else 2000 if "Window" in model else 5000


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=ROOT / "src/Cranberry.Zone/Data/Destructibles")
    parser.add_argument("--placements", type=Path, default=AUG / "out/world_aug/z2-objects.jsonl")
    parser.add_argument("--effects", type=Path, default=AUG / "out/data_aug/derived/effects.json")
    parser.add_argument("--zone", type=Path, default=AUG / "out/world_aug/Z2.zone")
    parser.add_argument("--health-reference", type=Path,
                        default=Path("C:/h1z1project/reference/h1z1-server/src/servers/ZoneServer2016/entities/destroyable.ts"))
    args = parser.parse_args()
    placements = args.placements.resolve()
    effects_path = args.effects.resolve()
    effects_doc = json.loads(effects_path.read_text())
    effects = {r["name"]: r["id"] for r in effects_doc["effects"]}
    types = []
    for model, effect in FAMILIES.items():
        types.append(dict(model=model + ".adr", effectId=effects[effect if effect.startswith("PFX_") else "PFX_Damage_" + effect],
                          health=health_for(model), instances=[]))
    by_model = {r["model"]: r for r in types}
    for line in placements.open():
        row = json.loads(line)
        if row["model"] in by_model:
            by_model[row["model"]]["instances"].append([row["id"], *row["pos"]])
    for row in types:
        assert row["instances"], row["model"]
        row["instances"].sort(key=lambda r: r[0])
    ids = [r[0] for t in types for r in t["instances"]]
    assert len(ids) == len(set(ids))
    dest = args.out / "z2-destructibles.json"
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_text(json.dumps(dict(schema=1, types=types), separators=(",", ":")) + "\n", encoding="utf-8", newline="\n")
    sources = [placements, effects_path, args.zone.resolve(), Path(__file__).resolve()]
    reference = args.health_reference.resolve()
    provenance = dict(grade="CLIENT placements/effects; SERVER health tuning", count=len(ids),
                      explosiveBarrelPolicy="User request 2026-09-08: red industrial barrels explode when shot; 1000 health is server tuning.",
                      wallBlockerPolicy=dict(modelId=9810, effectId=5298, health=4000,
                          grade="August authored actor/effect verified; health is declared server tuning",
                          note="Only WallHoleBlocked01 is removed. Surrounding WallHole01 model9809 is not destructible. "
                               "Reference segment0001 71.75-72.5s shows a shotgun wall break; initial health and pellet count are unknown. "
                               "Wall damage uses the existing weapon damage rules, not the fence five-bullet rule."),
                      healthSource=dict(path=str(reference), sha256=hashlib.sha256(reference.read_bytes()).hexdigest(),
                                        note="getMaxHealth: 2000 window / 5000 default, adopted tuning under D329; not retail proof"),
                      sources=[dict(path=str(p), sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in sources],
                      outputSha256=hashlib.sha256(dest.read_bytes()).hexdigest())
    dest.with_suffix(".provenance.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"{len(types)} families / {len(ids)} exact August Z2 terrain object ids -> {dest}")


if __name__ == "__main__":
    main()
