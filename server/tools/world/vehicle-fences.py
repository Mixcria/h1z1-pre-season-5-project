"""Generate August Z2 vehicle breakables from authored models, placements and effects.

The historical output name is retained for deployment compatibility. The explicit
allowlist covers light fences, road signs and fragile props with matching August
damage effects. It deliberately excludes concrete, military and structural blockers.
Speed loss and health are server tuning, not claimed retail collision values.
"""
import argparse
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
AUG = ROOT.parent

# Model basename -> (authored PFX_Damage_ suffix, tuned speed loss percent).
# Models.txt supplies the exact native ids, so BA/06, BA/05 and BA/03 cannot drift.
FAMILIES = {
    "Farm_Props_Fences_Fence01": ("Fence_Farm01", 10),
    "Farm_Props_Fences_Fence02": ("Fence_Farm02", 10),
    "Farm_Props_Fences_Post01": ("Fence_Farm01_Post", 10),
    "Farm_Props_Fences_FenceGate01": ("Fence_Farm01", 10),
    "Residential_Props_Fence_Tall01_Side01": ("Fence_ResidTall01", 20),
    "Residential_Props_Fence_Tall01_Side02": ("Fence_ResidTall02", 20),
    "Residential_Props_Fence_Tall01_Post01": ("Fence_ResidTall01", 20),
    "Residential_Props_Fence_Tall01_Gate01": ("Fence_ResidTallGate", 20),
    "Farm_Props_CorralFence_Fence01": ("Corral_Fence_01", 10),
    "Farm_Props_CorralFence_FenceBroken01": ("Corral_FenceBroken_01", 10),
    "Farm_Props_CorralFence_Gate01": ("Corral_Gate_01", 10),
    "Farm_Props_CorralFence_GateDoor01": ("Corral_GateDoor_01", 10),
    "Industrial_Props_ChainLinkFence_Straight": ("Fence_ChainLink_Industrial_Straight", 10),
    "Industrial_Props_ChainLinkFence_Post": ("Fence_ChainLink_Industrial_Post", 10),
    "Industrial_Props_ChainLinkFence_Corner": ("Fence_ChainLink_Industrial_Corner", 10),
    "Industrial_Props_ChainLinkFence_Gate": ("Fence_ChainLink_Industrial_Gate", 10),
    "Industrial_Props_ChainLinkFence_Breach": ("Fence_ChainLink_Industrial_Breach", 10),
    "Common_Props_BarbedWireFence1x1": ("Z1_BarbedWireFence1x1", 10),
    "Common_Props_BarbedWireFence1x2": ("Z1_BarbedWireFence1x2", 10),
    "City_Props_TrafficSigns_StopSigns": ("TrafficSigns_StopSign", 10),
    "City_Props_TrafficSigns_Town": ("TrafficSigns_Town", 10),
    "City_Props_TrafficSigns_HighWay": ("TrafficSigns_Highway", 10),
    "City_Props_TrafficSigns_HighWay_LargeSigns_Road_TwoPost01": ("TrafficSigns_Highway_TwoPosts", 20),
    "City_Props_TrafficSigns_HighWay_LargeSigns_Wood_TwoPost01": ("TrafficSigns_Highway_Wood_TwoPosts", 20),
    "City_Props_StreetSigns_StreetNames_Sign": ("TrafficSigns_StreetSign_StreetName", 10),
    "City_Props_StreetSigns_StreetNames_SignPost": ("TrafficSigns_StreetSign", 10),
    "City_Props_PostalBox01": ("Paper_Mail_PostalBox", 10),
    "Residential_Props_CommunalMailbox": ("Paper_Mail_CommunalMailbox", 10),
    "City_Props_NewsPaperVending": ("Paper_Newspapers_Vending", 10),
    "City_Props_ParkingMeter": ("ParkingMeter", 10),
    "City_Props_GarbageCan01": ("Paper_Trash_CityGarbageCan_01", 10),
    "Residential_Props_MunicipalGarbageCans": ("Paper_Trash_MunicipalGarbageCan", 10),
    "Industrial_Props_Barriers_CrowdBarrier01": ("Barriers_IndustrialCrowdBarrier", 10),
    "Industrial_Props_Crates_Crate01": ("Crate_Industrial", 10),
    "Industrial_Props_Crates_Crate02": ("Crate_Industrial", 10),
    "Commercial_Props_ShoppingCart": ("ShoppingCart", 10),
    "Farm_Props_Tools_Wheelbarrow01": ("Wheelbarrow_01", 10),
    "CampGround_Props_Cooler": ("Cooler_Campground", 10),
    "CampGround_Props_Tent01": ("Tent_Campground", 10),
    "Cabins_Props_Barrels_Basket01": ("Barrels_CabinBasket01", 10),
    "CampGround_Props_FoldingChair01": ("TableAndChairs_Campground_FoldingChair", 10),
    "Church_Props_FoldingChairs": ("TableAndChairs_Church_FoldingChair", 10),
    "Residential_Props_PatioFurnitureSet_Chair": ("TableAndChairs_Residential_PatioChair", 10),
    "Residential_Props_PatioFurnitureSet_Table": ("TableAndChairs_Residential_PatioTable", 10),
    "Restaurant_Props_CafeTableandChairs_Chair": ("TableAndChairs_Restaurant_CafeChair", 10),
    "Restaurant_Props_CafeTableandChairs_Table": ("TableAndChairs_Restaurant_CafeTable", 10),
    "Restaurant_Props_CafeTableandChairs_Umbrella": ("TableAndChairs_Restaurant_CafeUmbrella", 10),
    "Restaurant_Props_SquareTableAndChairs_Chair": ("TableAndChairs_Restaurant_SquareChair", 10),
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=ROOT / "src/Cranberry.Zone/Data/Destructibles")
    args = parser.parse_args()
    placements = AUG / "out/world_aug/z2-objects.jsonl"
    effects_path = AUG / "out/data_aug/derived/effects.json"
    models_path = AUG / "out/data_aug/Models.txt"
    effects = {r["name"]: r["id"] for r in json.loads(effects_path.read_text())["effects"]}
    models = {}
    for line in models_path.read_text().splitlines():
        if not line or line.startswith("#"):
            continue
        row = line.split("^")
        models.setdefault(row[1], []).append(int(row[0]))
    types = {}
    for basename, (effect, speed_loss) in FAMILIES.items():
        model = basename + ".adr"
        assert len(models.get(model, [])) == 1, f"Expected unique authored id for {model}"
        types[model] = dict(model=model, modelId=models[model][0],
                            effectId=effects["PFX_Damage_" + effect],
                            speedLossPercent=speed_loss, health=5000, instances=[])
    for line in placements.open():
        row = json.loads(line)
        if row["model"] in types:
            types[row["model"]]["instances"].append([row["id"], *row["pos"]])
    for row in types.values():
        # Z2 uses industrial chain link; these two legacy families have no placements.
        assert row["instances"] or "Common_Props_BarbedWireFence" in row["model"], row["model"]
        row["instances"].sort(key=lambda r: r[0])
    ids = [r[0] for t in types.values() for r in t["instances"]]
    assert len(ids) == len(set(ids)), "Duplicate terrain object id"
    dest = args.out / "z2-vehicle-fences.json"
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_text(json.dumps(dict(schema=2, types=list(types.values())), separators=(",", ":")) + "\n")
    sources = [placements, effects_path, models_path, Path(__file__)]
    dest.with_suffix(".provenance.json").write_text(json.dumps(dict(
        grade="CLIENT model ids, map placements and effects; SERVER allowlist, speed loss and health tuning",
        count=len(ids), families=len(types),
        sources=[dict(path=str(p), sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in sources],
        outputSha256=hashlib.sha256(dest.read_bytes()).hexdigest()), indent=2) + "\n")
    print(f"{len(types)} families / {len(ids)} vehicle breakable placements -> {dest}")


if __name__ == "__main__":
    main()
