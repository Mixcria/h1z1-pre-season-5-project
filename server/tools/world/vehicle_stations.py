"""Fill the two police-station omissions using checked individual August bays."""
import json
from pathlib import Path

from vehicle_geometry import DRIVABLE_MODELS, axes_for, box, overlaps

DEFAULT_STATIONS = Path(__file__).with_name("data") / "august-police-station-spawns.json"


def load_stations(path=DEFAULT_STATIONS):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def station_anchors(document, objects, area_boxes, obstacles, bounds, terrain):
    by_id = {record["id"]: record for record in objects}
    anchors = []
    for spawn in document["spawns"]:
        parking = by_id.get(spawn["parkingInstanceId"])
        if parking is None or parking["model"] != document["parkingModel"]:
            raise ValueError(f"Missing August parking bay for {spawn['station']}")
        for actual, expected in (("pos", "parkingPosition"), ("rot", "parkingRotation"), ("scale", "parkingScale")):
            if parking[actual] != spawn[expected]:
                raise ValueError(f"August parking transform changed for {spawn['station']}; recheck the bay")
        axes = axes_for(parking["rot"])
        local = document["localPosition"]
        x, y, z = [round(parking["pos"][i] + sum(axes[j][i] * local[j] * parking["scale"][j]
                                                 for j in range(3)), 5) for i in range(3)]
        yaw = parking["rot"][0] + document["localYaw"]
        car_bounds = bounds[DRIVABLE_MODELS[3]]
        candidate = box(car_bounds, (x, y, z), (yaw, 0, 0))
        if any(overlaps(candidate, obstacle) for obstacle in obstacles):
            raise ValueError(f"Police-station bay overlaps a static vehicle: {spawn['station']}")
        if terrain is not None and terrain.is_buried(car_bounds, (x, y, z), yaw):
            raise ValueError(f"Police-station bay is buried: {spawn['station']}")
        area = next((name for name, x0, z0, x1, z1 in area_boxes if x0 <= x <= x1 and z0 <= z <= z1), None)
        anchors.append(dict(id=spawn["id"], x=x, y=y, z=z, yaw=yaw, spaces=1, area=area,
                            vehicleId=3, station=spawn["station"],
                            augustParkingInstanceId=spawn["parkingInstanceId"]))
    return anchors
