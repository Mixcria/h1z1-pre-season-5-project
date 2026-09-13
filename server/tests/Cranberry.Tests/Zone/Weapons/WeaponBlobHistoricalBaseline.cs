using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Weapons;

/// <summary>Keeps earlier table audits pinned to their original bytes as later fixes ship.</summary>
internal static class WeaponBlobHistoricalBaseline
{
    public static byte[] BeforeBinocularFireGuard(WeaponDefinitionsBlob current)
    {
        // The September 6 fix adds CHECK_ENTER_FIRE_STATE and AMMO_PER_SHOT=1 to the
        // two modes of binocular groups 21/22. Removing only those eight bytes must
        // reproduce each historical hash; the actual guard is covered by BinocularMagazineTests.
        FireModeRecord[] optics = current.FireModes!
            .Where(mode => mode.FireModeId is 42 or 43 or 44 or 45).ToArray();
        Assert.Equal(4, optics.Length);
        Assert.All(optics, mode =>
        {
            Assert.True(mode.CheckEnterFireState);
            Assert.Equal(1, mode.AmmoPerShot);
        });

        byte[] before = (current with
        {
            FireModes = current.FireModes!.Select(mode => mode.FireModeId is 42 or 43 or 44 or 45
                ? mode with { CheckEnterFireState = false, AmmoPerShot = 0 }
                : mode).ToArray(),
        }).ToArray();
        byte[] after = current.ToArray();
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(8, before.Zip(after).Count(pair => pair.First != pair.Second));
        return before;
    }
}
