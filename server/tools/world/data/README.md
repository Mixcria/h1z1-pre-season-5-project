# Z2 authored vehicle locations

`z2-vehicle-locations.json` is an unchanged copy of the owner's local
`C:/Z1/Data/2017/zoneData/Z2_vehicleLocations.json` (403 rows), reused under D329.
It is reference data, not an extraction from this August client's zone.

`../vehicle_anchors.py` retains each location's origin, heading and vehicle family,
except that surviving invisible August vehicle proxies supply their exact current
transforms. It joins August named areas and rejects overlap with the oriented bounds
of static vehicle meshes. Solid August terrain also excludes pads whose entire vehicle
is buried below the surface; bridges and raised roads retain their authored height.
The shipped result has 257 locations. See the September 7 obstruction/station
audit and the September 11 capture adoption. Default occupancy is still 100%.
No full building/road/bridge collision validation is claimed.

`august-vehicle-mesh-bounds.json` contains the bounds of 14 shipped client meshes:
ten static vehicle families and four drivable families. Regenerate it with
`python -X utf8 tools/world/vehicle_geometry.py`. The tool reads each ADR's Base DME,
verifies its pack CRC, and reads the six bounds floats after its material block.
The file records mesh names and CRCs. Bounds provide conservative exclusion volumes;
they are not the vehicle's detailed CDT/APX collision mesh. See
`docs/vehicle-spawns-20260905.md` and `docs/vehicle-spawns-20260906.md` for the measured
changes and limitations.

`z1br-vehicle-observations.json` contains 44 vehicle observations from the owner's
official Z1BR recording, decoded offline by `tools/diagnostics/decode_z1br_vehicles.py`.
Only two are on main Z2: 23 belong to PracticeZone and 19 to the elevated pregame
area. Raw authentication traffic, keys, tickets and player identities are excluded.
`z1br-vehicle-august-adoption.json` records the explicit choice to replace the nearby
West Peaks jeep and add the barn ATV, the exact captured quaternion/position and
August geometry audit. These later-retail observations do not certify the complete
August spawn map or its probabilities. See `docs/vehicle-spawns-capture-20260911.md`.
