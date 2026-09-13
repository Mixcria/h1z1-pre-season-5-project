using System.Security.Cryptography;
using Cranberry.Protocol;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Weapons;

/// <summary>
/// docs/121 - the two <c>WeaponDefinitions</c> changes of the shooting audit: the retail
/// automatic set (D331: the AK-47 family is automatic and the AR-15 is not, with
/// <c>AUTO_FIRE_TIME_MS</c> and the mode's <c>AUTOMATIC</c> bit written beside the group's flag)
/// and the 12GA's pellet count and spread (D332). Every byte is pinned where the client reads it,
/// and every switch's off arm is byte-identical to the wave before.
/// </summary>
public sealed class ShootingRetailBlobTests
{
    private const uint Ar15Group = 6;
    private const uint Ak47Group = 51;
    private const uint ModifiedAk47Group = 77;
    private const uint ThirdAkGroup = 82;
    private const uint ShotgunGroup = 16;

    /// <summary>The shipped default blob - every stage on, this lane's two passes included.</summary>
    private const string ShippedBlobSha256 =
        "67b85d42a257e43a2971ffa1237af0b5ef46629a88d0e6828a8e2225372c8a9a";

    private const int BlobLength = 89801;

    /// <summary>
    /// The automatic set is exactly the AK-47 family - the fire groups of every <c>WEAPON_ID</c>
    /// the client pairs with the 7.62x39 round - and the AR-15's group 6 is not in it. The
    /// shotgun set is the 12GA's one group.
    /// </summary>
    [Fact]
    public void TheRetailAutomaticSetIsTheAk47FamilyAndNothingElse()
    {
        Assert.Equal(
            new HashSet<uint> { Ak47Group, ModifiedAk47Group, ThirdAkGroup },
            AugustWeaponTable.RetailAutomaticFireGroupIds);
        Assert.DoesNotContain(Ar15Group, AugustWeaponTable.RetailAutomaticFireGroupIds);
        Assert.Equal(new HashSet<uint> { ShotgunGroup }, AugustWeaponTable.ShotgunFireGroupIds);

        // The wave-9 diagnostic set is untouched: it is the off arm.
        Assert.Equal(new HashSet<uint> { Ar15Group }, AugustWeaponTable.AutomaticFireGroupIds);
    }

    /// <summary>
    /// List 1: the retail records carry the automatic bit (<c>rec+0x38</c> bit 6, the branch
    /// <c>FUN_1411ceca0</c> takes) on the three AK groups and on nothing else - the AR-15, the
    /// fists and the R380 are single-shot.
    /// </summary>
    [Fact]
    public void TheRetailFireGroupsMarkTheAk47AutomaticAndTheAr15SingleShot()
    {
        Dictionary<uint, FireGroupRecord> byId =
            AugustWeaponTable.RetailFireGroupRecords.ToDictionary(record => record.FireGroupId);

        Assert.True(byId[Ak47Group].IsAutomatic);
        Assert.True(byId[ModifiedAk47Group].IsAutomatic);
        Assert.True(byId[ThirdAkGroup].IsAutomatic);
        Assert.False(byId[Ar15Group].IsAutomatic);
        Assert.False(byId[12].IsAutomatic);     // fists
        Assert.False(byId[38].IsAutomatic);     // R380
        Assert.Equal(3, AugustWeaponTable.RetailFireGroupRecords.Count(record => record.IsAutomatic));
        Assert.Equal(
            AugustWeaponTable.FireGroupRecords.Count,
            AugustWeaponTable.RetailFireGroupRecords.Count);
    }

    /// <summary>
    /// List 2: an automatic group's modes carry <c>AUTOMATIC</c> (<c>rec+0x20</c> bit <c>0x20</c>)
    /// and an <c>AUTO_FIRE_TIME_MS</c> (<c>rec+0x48</c>) equal to their <c>REFIRE_TIME_MS</c> -
    /// 160 ms on the AK-47 - because <c>FUN_14228d2d0</c> reads <c>rec+0x48</c> for every shot of
    /// a hold after the second and clamps it to 1 ms. The AR-15's modes carry neither.
    /// </summary>
    [Fact]
    public void TheAutomaticModesCarryTheBitAndAnAutoFireTimeEqualToTheirRefireTime()
    {
        IReadOnlyList<FireModeRecord> records = AugustWeaponTable.FireModeRecordsFor(
            adsZoom: 1.0f, adsFirstPerson: false, fpCameraFovDegrees: 0f, retailAutomatic: true);

        FireModeRecord akHip = records.Single(r => r.FireModeId == AugustWeaponTable.FireModeIdFor(Ak47Group, 0));
        FireModeRecord akAds = records.Single(r => r.FireModeId == AugustWeaponTable.FireModeIdFor(Ak47Group, 1));
        FireModeRecord arHip = records.Single(r => r.FireModeId == AugustWeaponTable.FireModeIdFor(Ar15Group, 0));

        Assert.True(akHip.Automatic);
        Assert.True(akAds.Automatic);
        Assert.Equal(160, akHip.RefireTimeMs);
        Assert.Equal(160, akHip.AutoFireTimeMs);
        Assert.Equal(160, akAds.AutoFireTimeMs);
        Assert.False(arHip.Automatic);
        Assert.Equal(0, arHip.AutoFireTimeMs);
        Assert.Equal(120, arHip.RefireTimeMs);

        // Where the client reads them: body offset 0 is rec+0x20 (the flag byte), body offset 18
        // is rec+0x48 (AUTO_FIRE_TIME_MS, i16) - docs/99 section 2.3 rows 1 and 12.
        byte[] bytes = Bytes(akAds.WriteTo);
        int body = 8;
        Assert.Equal(
            WeaponListLayouts.FireModeAutomaticFlag | WeaponListLayouts.FireModeIronSightsFlag,
            bytes[body + 0]);
        Assert.Equal(160, BitConverter.ToInt16(bytes, body + 18));

        byte[] ar = Bytes(arHip.WriteTo);
        Assert.Equal(0, ar[body + 0]);
        Assert.Equal(0, BitConverter.ToInt16(ar, body + 18));
        Assert.Equal(FireModeRecord.RecordLength, bytes.Length);
    }

    /// <summary>
    /// The 12GA's two modes carry <c>PELLETS_PER_SHOT</c> 17 (<c>rec+0x74</c>, body offset 37) and
    /// <c>PELLET_SPREAD</c> 4.0 (<c>rec+0x7c</c>, body offset 39, f32) with the shipped levers,
    /// and no other mode does.
    /// </summary>
    [Fact]
    public void TheShotgunModesCarrySeventeenPelletsAndTheSpread()
    {
        IReadOnlyList<FireModeRecord> records = AugustWeaponTable.FireModeRecordsFor(
            adsZoom: 1.0f, adsFirstPerson: false, fpCameraFovDegrees: 0f,
            shotgunPellets: RetailBalance.RetailShotgunPellets, shotgunSpreadDegrees: 4.0f);

        FireModeRecord pump = records.Single(r => r.FireModeId == AugustWeaponTable.FireModeIdFor(ShotgunGroup, 0));
        Assert.Equal(17, pump.PelletsPerShot);
        Assert.Equal(4.0f, pump.PelletSpread);

        byte[] bytes = Bytes(pump.WriteTo);
        int body = 8;
        Assert.Equal(17, bytes[body + 37]);
        Assert.Equal(4.0f, BitConverter.ToSingle(bytes, body + 39));

        Assert.Equal(
            2,
            records.Count(r => r.PelletsPerShot != 0 || r.PelletSpread != 0f));
    }

    /// <summary>
    /// The whole shipped blob: same length as every wave since 14, the retail passes ON by
    /// default, and turning both off restores the wave-16 bytes exactly (the pin
    /// <c>WeaponListColumnTableTests</c> keeps).
    /// </summary>
    [Fact]
    public void TheShippedBlobKeepsItsLengthAndTheOffArmsRestoreTheBytes()
    {
        Assert.True(WeaponStageOptions.Default.RetailAutomatic);
        Assert.Equal(RetailBalance.RetailShotgunPellets, WeaponStageOptions.Default.ShotgunPellets);
        Assert.Equal(4.0f, WeaponStageOptions.Default.ShotgunSpreadDegrees);

        // The fire-mode TYPE pass (D291-D293, the same morning) is cleared on both sides so this
        // pin isolates the retail-automatic and pellet bytes, the way the wave-16 pins do.
        byte[] shipped = WeaponBlobHistoricalBaseline.BeforeBinocularFireGuard(
            new WeaponSession(WeaponStageOptions.Default with
        {
            WriteFireModeTypes = false,
            // docs/120: the throwables add records and move the length; ThrowableTableTests pins
            // the default, this test isolates docs/121's own two passes.
            Throwables = false,
            // docs/123 (D313): the captured table crosses the friend's own recoil, cone and
            // reload words into list 2 and fills lists 3 and 5, so this byte pin stays on the
            // GENERATED table - CapturedWeaponTableTests pins the captured one.
            WeaponTable = WeaponTableSource.Generated,
        }).Blob);
        byte[] before = WeaponBlobHistoricalBaseline.BeforeBinocularFireGuard(
            new WeaponSession(WeaponStageOptions.Default with
        {
            WriteFireModeTypes = false,
            RetailAutomatic = false,
            ShotgunPellets = 0,
            ShotgunSpreadDegrees = 0.0f,
            Throwables = false,
            // docs/123 (D313): the captured table crosses the friend's own recoil, cone and
            // reload words into list 2 and fills lists 3 and 5, so this byte pin stays on the
            // GENERATED table - CapturedWeaponTableTests pins the captured one.
            WeaponTable = WeaponTableSource.Generated,
        }).Blob);

        Assert.Equal(BlobLength + AugustShotgunPattern.Pattern.Length + AugustShotgunPattern.Group.Length, shipped.Length);
        Assert.Equal(BlobLength, before.Length);
        Assert.NotEqual(before, shipped);
        Assert.Equal(ShippedBlobSha256, Sha256(shipped));

        // The automatic flag master switch still clears everything, retail set included.
        byte[] noAuto = WeaponBlobHistoricalBaseline.BeforeBinocularFireGuard(
            new WeaponSession(WeaponStageOptions.Default with
        {
            MarkFireGroupsAutomatic = false,
            Throwables = false,
            // docs/123 (D313): the captured table crosses the friend's own recoil, cone and
            // reload words into list 2 and fills lists 3 and 5, so this byte pin stays on the
            // GENERATED table - CapturedWeaponTableTests pins the captured one.
            WeaponTable = WeaponTableSource.Generated,
        }).Blob);
        Assert.Equal(shipped.Length, noAuto.Length);
        Assert.NotEqual(shipped, noAuto);
    }

    [Fact]
    public void TheThreeLeversReadTheEnvironment()
    {
        WeaponStageOptions off = WeaponStageOptions.FromEnvironment(name => name switch
        {
            WeaponStageOptions.RetailAutomaticVariable => "0",
            WeaponStageOptions.ShotgunPelletsVariable => "12",
            WeaponStageOptions.ShotgunSpreadVariable => "2.5",
            _ => null,
        });

        Assert.False(off.RetailAutomatic);
        Assert.Equal(12, off.ShotgunPellets);
        Assert.Equal(2.5f, off.ShotgunSpreadDegrees);

        WeaponStageOptions typo = WeaponStageOptions.FromEnvironment(name => name switch
        {
            WeaponStageOptions.RetailAutomaticVariable => "no",
            WeaponStageOptions.ShotgunPelletsVariable => "eight",
            _ => null,
        });
        Assert.True(typo.RetailAutomatic);
        Assert.Equal(RetailBalance.RetailShotgunPellets, typo.ShotgunPellets);

        Assert.False(WeaponStageOptions.AllOff.RetailAutomatic);
        Assert.Equal(0, WeaponStageOptions.AllOff.ShotgunPellets);
    }

    [Fact]
    public void TheBannerNamesTheAutomaticGunAndThePellets()
    {
        string line = new WeaponSession().Describe();
        Assert.Contains("autoRetail=AK-47", line, StringComparison.Ordinal);
        Assert.Contains("shotgunPellets=17", line, StringComparison.Ordinal);
        Assert.Contains("shotgunSpread=4deg", line, StringComparison.Ordinal);

        string diagnostic = new WeaponSession(WeaponStageOptions.Default with { RetailAutomatic = false })
            .Describe();
        Assert.Contains("autoRetail=AR-15(diag)", diagnostic, StringComparison.Ordinal);
    }

    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}
