namespace Cranberry.Zone.Vehicles;

/// <summary>
/// Population policy over confirmed retail marker IDs, without changing their poses or families.
/// The owner reports one car, sometimes two, at a police station (2026-09-12).
/// See docs/vehicle-randomness-20260912.md for the chosen odds and their evidence limits.
/// </summary>
internal static class VehiclePopulationGroups
{
    private static readonly uint[][] PoliceStations =
    [
        // Pleasant Valley police department, not the surrounding PVCommercialEast area.
        [2923626521, 3301651757, 3720173606, 3023995757],
        // Ranchito police department; nearby pickups and offroaders remain independent.
        [3202695640, 847242019, 1114722019, 847242020],
    ];

    internal static void LimitPoliceStations(VehicleAnchorSet anchors, int[] order, List<int> chosen)
    {
        var occupied = new bool[anchors.Count];
        foreach (int index in chosen) occupied[index] = true;

        foreach (uint[] station in PoliceStations)
        {
            int firstBay = -1;
            int count = 0;
            // The match-seeded shuffle randomizes both the retained bays and a zero-roll fallback.
            // At the default p=.3, clamping four rolls to [1,2] gives a second car about 35% of rounds.
            foreach (int index in order)
            {
                ref readonly VehicleAnchor anchor = ref anchors[index];
                if (anchor.VehicleId != 3 || !station.Contains(anchor.InstanceId)) continue;
                if (firstBay < 0) firstBay = index;
                if (occupied[index] && ++count > 2) occupied[index] = false;
            }

            if (count == 0 && firstBay >= 0) occupied[firstBay] = true;
        }

        chosen.Clear();
        foreach (int index in order)
            if (occupied[index]) chosen.Add(index);
    }
}
