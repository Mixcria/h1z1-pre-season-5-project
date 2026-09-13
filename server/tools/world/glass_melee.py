"""Extract oriented August window panes for server melee contact, without spawning actors."""
import hashlib
import argparse
import json
from pathlib import Path
import struct
import sys
import xml.etree.ElementTree as ET

from vehicle_geometry import box

ROOT = Path(__file__).resolve().parents[2]
AUG = ROOT.parent
sys.path.insert(0, str(ROOT / "tools/pack"))
import packread


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=ROOT / "src/Cranberry.Zone/Data/Destructibles")
    args = parser.parse_args()
    entries, _ = packread.build_index(packread.find_packs(packread.DEFAULT_ASSETS_DIR))
    index = {e.name: e for e in entries}
    models = {}
    for name in ("Common_Props_GlassWindow01.adr", "Common_Props_TintedWindow01.adr"):
        adr = packread.read_asset_bytes(index[name])
        mesh = ET.fromstring(adr).find("Base").get("fileName")
        data = packread.read_asset_bytes(index[mesh])
        assert data[:4] == b"DMOD"
        bounds = struct.unpack_from("<6f", data, 12 + struct.unpack_from("<I", data, 8)[0])
        assert abs(bounds[3] - bounds[0]) < 0.001  # both authored panes lie in local YZ
        models[name] = dict(mesh=mesh, bounds=bounds, adrSha256=hashlib.sha256(adr).hexdigest(),
                            meshSha256=hashlib.sha256(data).hexdigest())
    placements = AUG / "out/world_aug/z2-objects.jsonl"
    panes = []
    for line in placements.open():
        row = json.loads(line)
        if row["model"] not in models:
            continue
        center, axes, extent = box(models[row["model"]]["bounds"], row["pos"], row["rot"], row["scale"])
        panes.append([row["id"], *center, *axes[0], *axes[1], extent[1], extent[2]])
    panes.sort(key=lambda p: p[0])
    dest = args.out / "z2-glass-panes.json"
    dest.parent.mkdir(parents=True, exist_ok=True)
    doc = dict(schema=1, columns="id,centerXYZ,normalXYZ,upXYZ,halfHeight,halfWidth", panes=panes)
    dest.write_text(json.dumps(doc, separators=(",", ":")) + "\n")
    sources = [placements, Path(__file__), ROOT / "tools/world/vehicle_geometry.py"]
    provenance = dict(grade="CLIENT ADR/DME planar bounds and Z2 placement transforms", models=models,
        count=len(panes), sources=[dict(path=str(p), sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in sources],
        outputSha256=hashlib.sha256(dest.read_bytes()).hexdigest())
    dest.with_suffix(".provenance.json").write_text(json.dumps(provenance, indent=2) + "\n")
    print(f"{len(panes)} oriented glass panes -> {dest}")


if __name__ == "__main__":
    main()
