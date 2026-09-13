using System.Globalization;
using System.Security.Cryptography;
using Cranberry.Zone;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Weapons;

/// <summary>
/// <b>The list-2 <c>FireModes</c> column map, pinned against the client's own datasheet-row loader.</b>
/// <para>
/// <see cref="WeaponListLayouts.FireModeColumns"/> and the two hundred
/// <c>FireMode&lt;Column&gt;</c> constants beside it are a naming layer over
/// <c>H1Z1.exe 0x142226260..0x14222a800</c> - see docs/99 §2.3 (CORRECTION 3) and docs/107 §9.3.
/// The extraction lives in three TSVs under <c>tools/data</c>, and these tests re-parse those
/// files and assert the C# agrees, so the copies under <c>C:\Aug2017\out\w14-adsfov</c> are never
/// load-bearing at runtime and a hand edit to either side is a failing test.
/// </para>
/// <para>
/// <b>This wave changed no behaviour.</b>
/// <see cref="TheShippedBlobIsByteIdenticalToTheWaveBeforeTheColumnNames"/> pins the shipped
/// <c>WeaponDefinitions</c> blob to the sha256 it had before a single name was added.
/// </para>
/// </summary>
public sealed class WeaponListColumnTableTests
{
    /// <summary>
    /// The sha256 of the shipped default blob (wave 16's iron-sights-armed / melee-ability passes
    /// cleared to isolate this lane): <c>adsZoom = 1.2</c>, ADS first person OFF, the 266 ms
    /// iron-sights ramp ON, and the fire-effect <c>EFFECT_GROUP</c> (report 1) ON - the
    /// 2026-09-03-evening third-person-ADS + fire-sound shape. Re-baselined for reports 1 and 4.
    /// </summary>
    private const string ShippedBlobSha256 =
        "76817cf20f48b510d1eef9c98fcdbf8811520a0dcb9f24fbd25f536483907239";

    /// <summary>
    /// The same blob with the iron-sights ramp off (<c>CRANBERRY_WEAPON_ADS_TIMES=0</c>): the ADS
    /// zoom 1.2 third-person shape with the fire effect on, byte-identical to
    /// <c>CreateBlob(adsZoom: 1.2)</c>.
    /// </summary>
    private const string Wave14BlobSha256 =
        "c426d1cf45449f202a77b961203765089d1fd6e5aacbbf0c8a6726587dfb6e76";

    /// <summary>
    /// The same blob with the ADS zoom off (<c>adsZoom = 1.0f</c>, the client's own constructor
    /// preset) but the fire effect still on - <c>CreateBlob(populateWeaponDefinitions: true)</c>.
    /// </summary>
    private const string PlainBlobSha256 =
        "e688df961f15c778502c6ec0be4e64f5a885d2dcd23aed09966cae1deebccaa3";

    private const int BlobLength = 89801;

    /// <summary>
    /// <b>The shipped blob is byte-pinned.</b> Reports 1 and 4 deliberately move bytes (the fire
    /// <c>EFFECT_GROUP</c> and the ADS zoom 1.2 / FP-off), so these shas were re-baselined on
    /// 2026-09-03 evening; the <b>length is unchanged</b> (89801) because every field in play was
    /// already on the wire as a zero. Wave 16's iron-sights-armed and melee-ability passes are
    /// cleared here so a change to either does not re-baseline this lane (<c>WeaponListsTests</c>
    /// covers them).
    /// </summary>
    [Fact]
    public void TheShippedBlobIsByteIdenticalToTheWaveBeforeTheColumnNames()
    {
        byte[] shipped = WeaponBlobHistoricalBaseline.BeforeBinocularFireGuard(
            new WeaponSession(WeaponStageOptions.Default with
        {
            IronSightsArmedOnly = false,
            WriteMeleeAbilityIds = false,
            WriteFireModeTypes = false,
            // docs/121 (D331 / D332): the retail automatic set and the shotgun pellets move
            // bytes of list 1 and list 2; cleared here for the same reason wave 16's passes are,
            // and pinned in ShootingRetailBlobTests instead.
            RetailAutomatic = false,
            ShotgunPellets = 0,
            ShotgunSpreadDegrees = 0.0f,
            // docs/120: the throwables add records (a group, a weapon, two modes, ten list-4
            // rows), so the wave-16 control clears them too; ThrowableTableTests pins them.
            // docs/123 (D313): the captured table crosses the friend's own recoil, cone and
            // reload words into list 2 and fills lists 3 and 5, so this byte pin stays on the
            // GENERATED table - CapturedWeaponTableTests pins the captured one.
            WeaponTable = WeaponTableSource.Generated,
            Throwables = false,
        }).Blob);
        byte[] plain = WeaponBlobHistoricalBaseline.BeforeBinocularFireGuard(
            AugustWeaponTable.CreateBlob(populateWeaponDefinitions: true));
        byte[] ads = WeaponBlobHistoricalBaseline.BeforeBinocularFireGuard(AugustWeaponTable
            .CreateBlob(populateWeaponDefinitions: true, adsZoom: AugustFireModeFacts.AdsZoom));

        Assert.Equal(BlobLength, shipped.Length);
        Assert.Equal(BlobLength, plain.Length);
        Assert.Equal(PlainBlobSha256, Sha256(plain));
        Assert.Equal(Wave14BlobSha256, Sha256(ads));
        Assert.Equal(ShippedBlobSha256, Sha256(shipped));

        // Turning the iron-sights ramp off (CRANBERRY_WEAPON_ADS_TIMES=0) leaves the zoom-1.2
        // third-person shape, byte-identical to CreateBlob(adsZoom: 1.2).
        Assert.Equal(
            Wave14BlobSha256,
            Sha256(WeaponBlobHistoricalBaseline.BeforeBinocularFireGuard(
                new WeaponSession(WeaponStageOptions.Default with
            {
                WriteAdsFirstPerson = false,
                WriteIronSightsTimes = false,
                IronSightsArmedOnly = false,
                WriteMeleeAbilityIds = false,
                WriteFireModeTypes = false,
                RetailAutomatic = false,
                ShotgunPellets = 0,
                ShotgunSpreadDegrees = 0.0f,
                Throwables = false,
                // docs/123 (D313): this shape is the generated table's, as it was before the
                // captured crossing existed.
                WeaponTable = WeaponTableSource.Generated,
            }).Blob)));
    }


    /// <summary>
    /// <c>tools/data/firemodes-columns.tsv</c> - the 134 columns the loader stores with a
    /// <c>MOVSS</c> (float) or a <c>MOV byte</c> - is reproduced exactly by
    /// <see cref="WeaponListLayouts.FireModeColumns"/>, and every row's <c>recOff</c> really is
    /// its <c>loaderOff + 0x20</c> (the loader's object base is the record's <c>rec+0x20</c>).
    /// </summary>
    [Fact]
    public void TheFloatAndByteColumnsMatchTheLoaderExtraction()
    {
        IReadOnlyList<Row> rows = ReadTsv("firemodes-columns.tsv");
        Assert.Equal(134, rows.Count);

        foreach (Row row in rows)
        {
            Assert.Equal(row.RecordOffset, row.LoaderOffset + 0x20);
            FireModeColumnKind kind = row.Width switch
            {
                "dword" => FireModeColumnKind.Float,
                "byte" => FireModeColumnKind.Byte,
                _ => throw new InvalidOperationException($"unknown width {row.Width}"),
            };

            Assert.Contains(
                new FireModeColumn(row.Name, (short)row.RecordOffset, kind),
                WeaponListLayouts.FireModeColumns);
        }

        Assert.Equal(
            129, WeaponListLayouts.FireModeColumns.Count(c => c.Kind == FireModeColumnKind.Float));
        Assert.Equal(
            5, WeaponListLayouts.FireModeColumns.Count(c => c.Kind == FireModeColumnKind.Byte));
    }

    /// <summary>
    /// <c>tools/data/firemodes-columns-ids.tsv</c> - the 45 integer/id/duration columns the loader
    /// stores through its own string-to-int helper (<c>LEA RDX,[RBX + off]</c> then
    /// <c>CALL 0x14007022f</c>) rather than with a <c>MOVSS</c>. This is the route that binds
    /// <c>REFIRE_TIME_MS</c>, the five reload columns, <c>AMMO_SLOT</c> and the heat family.
    /// </summary>
    [Fact]
    public void TheIntegerColumnsMatchTheLoaderExtraction()
    {
        IReadOnlyList<Row> rows = ReadTsv("firemodes-columns-ids.tsv");
        Assert.Equal(45, rows.Count);

        foreach (Row row in rows)
        {
            Assert.Equal("int", row.Width);
            Assert.Equal(row.RecordOffset, row.LoaderOffset + 0x20);
            Assert.Contains(
                new FireModeColumn(row.Name, (short)row.RecordOffset, FireModeColumnKind.Int),
                WeaponListLayouts.FireModeColumns);
        }

        Assert.Equal(
            45, WeaponListLayouts.FireModeColumns.Count(c => c.Kind == FireModeColumnKind.Int));
    }

    /// <summary>
    /// <c>tools/data/firemodes-columns-full.tsv</c>'s bit-packed rows - the 21 flags of
    /// <c>rec+0x20</c>, <c>rec+0x21</c> and <c>rec+0x22</c>.
    /// <para>
    /// <b>One row of that file is excluded, on purpose.</b> It carries 22 <c>OR</c>/<c>AND</c>
    /// rows, and the twenty-second is <c>TP_CAMERA_INDOOR_OUTDOOR_SPEED</c> at <c>rec+0x022</c>
    /// bit <c>0x40</c>. The disassembly says otherwise: the site at <c>14222980e</c> is
    /// <c>MOVSS dword ptr [RBX + 0x2c8],XMM1</c>, i.e. a float at <c>rec+0x2e8</c>, exactly as
    /// <c>firemodes-columns.tsv</c> and docs/107 §9.3 already have it. Where the two extractions
    /// disagree the value table wins, and <see cref="TheOneDisagreementBetweenTheTwoTsvsIsPinned"/>
    /// pins that decision rather than leaving it silent.
    /// </para>
    /// </summary>
    [Fact]
    public void TheFlagBitsMatchTheLoaderExtraction()
    {
        HashSet<string> valueLeas = [.. ReadTsv("firemodes-columns.tsv").Select(r => r.Lea)];
        List<FullRow> flags = [.. ReadFullTsv().Where(r => r.Bit is not null)];
        Assert.Equal(22, flags.Count);

        List<FullRow> real = [.. flags.Where(r => !valueLeas.Contains(r.Lea))];
        Assert.Equal(21, real.Count);

        foreach (FullRow row in real)
        {
            Assert.InRange(row.RecordOffset, 0x20, 0x22);
            Assert.Contains(
                new FireModeColumn(
                    row.Name, (short)row.RecordOffset, FireModeColumnKind.FlagBit, row.Bit!.Value),
                WeaponListLayouts.FireModeColumns);
        }

        Assert.Equal(
            21, WeaponListLayouts.FireModeColumns.Count(c => c.Kind == FireModeColumnKind.FlagBit));

        // Eight bits of rec+0x20, eight of rec+0x21, five of rec+0x22 - and rec+0x22 is the only
        // byte with holes (0x40, 0x20 and 0x01 are never written).
        Assert.Equal(8, real.Count(r => r.RecordOffset == WeaponListLayouts.FireModeFlags0));
        Assert.Equal(8, real.Count(r => r.RecordOffset == WeaponListLayouts.FireModeFlags1));
        Assert.Equal(5, real.Count(r => r.RecordOffset == WeaponListLayouts.FireModeFlags2));
    }

    /// <summary>
    /// The two ADS-lane TSVs disagree about exactly one loader site, and the disagreement is
    /// recorded here so it cannot quietly change sides.
    /// </summary>
    [Fact]
    public void TheOneDisagreementBetweenTheTwoTsvsIsPinned()
    {
        Row value = ReadTsv("firemodes-columns.tsv")
            .Single(r => r.Name == "TP_CAMERA_INDOOR_OUTDOOR_SPEED");
        FullRow[] full = [.. ReadFullTsv().Where(r => r.Name == "TP_CAMERA_INDOOR_OUTDOOR_SPEED")];

        // The full TSV lists the ONE loader site twice: once correctly as the dword it is, and
        // once again as a bit of rec+0x22. Both rows name the same lea, which is what makes the
        // spurious one detectable at all.
        Assert.Equal(2, full.Length);
        FullRow flag = full.Single(r => r.Bit is not null);

        Assert.Equal("14222980e", value.Lea);
        Assert.Equal("14222980e", flag.Lea);
        Assert.Equal(0x2e8, value.RecordOffset);          // MOVSS [RBX + 0x2c8]
        Assert.Equal(0x2e8, full.Single(r => r.Bit is null).RecordOffset);
        Assert.Equal(0x022, flag.RecordOffset);           // the misfiled bit row
        Assert.Equal((byte)0x40, flag.Bit!.Value);

        FireModeColumn shipped = WeaponListLayouts.FireModeColumns
            .Single(c => c.Name == "TP_CAMERA_INDOOR_OUTDOOR_SPEED");
        Assert.Equal(FireModeColumnKind.Float, shipped.Kind);
        Assert.Equal(0x2e8, shipped.RecordOffset);
        Assert.Equal(WeaponListLayouts.FireModeTpCameraIndoorOutdoorSpeed, shipped.RecordOffset);
    }

    /// <summary>
    /// The table is exactly 200 entries and its only repeated name is the one the client itself
    /// repeats: <c>MELEE_COMPOSITE_EFFECT_ID</c>, read once as a bool into <c>rec+0x22</c> bit
    /// <c>0x04</c> (loader <c>142227659</c>) and once as an id into <c>rec+0x190</c> (loader
    /// <c>142228730</c>), both from the same string at <c>0x1435e9178</c>.
    /// </summary>
    [Fact]
    public void TheTableIsTwoHundredColumnsWithOneDeliberateDuplicate()
    {
        Assert.Equal(200, WeaponListLayouts.FireModeColumns.Count);

        string[] repeated = [.. WeaponListLayouts.FireModeColumns
            .GroupBy(c => c.Name)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)];
        Assert.Equal(["MELEE_COMPOSITE_EFFECT_ID"], repeated);

        Assert.Equal(
            WeaponListLayouts.FireModeFlags2,
            WeaponListLayouts.FireModeColumns
                .Single(c => c.Name == "MELEE_COMPOSITE_EFFECT_ID"
                             && c.Kind == FireModeColumnKind.FlagBit).RecordOffset);
        Assert.Equal(
            WeaponListLayouts.FireModeMeleeCompositeEffectId,
            WeaponListLayouts.FireModeColumns
                .Single(c => c.Name == "MELEE_COMPOSITE_EFFECT_ID"
                             && c.Kind == FireModeColumnKind.Int).RecordOffset);
    }

    /// <summary>
    /// <b>The cross-check that makes the whole map credible.</b> Every column the loader stores is
    /// either a field the wire body writes, or one of the two class members
    /// <c>FUN_140a422d0</c> demonstrably skips - <c>CHARGE_UP_TIME_MS</c> (<c>rec+0x04c</c>) and
    /// <c>SPIN_UP_TIME_MS</c> (<c>rec+0x054</c>), which fall in the body's two four-byte holes.
    /// Two independent decodes - the network body reader and the datasheet loader - agree on the
    /// object's shape, and they disagree in exactly the places a sheet-only field would.
    /// </summary>
    [Fact]
    public void EveryLoaderColumnIsAFieldTheWireBodyWritesOrOneOfTheTwoSheetOnlyHoles()
    {
        HashSet<short> body = [.. WeaponListLayouts.FireModeBody.Select(f => f.RecordOffset)];
        string[] sheetOnly = [.. WeaponListLayouts.FireModeColumns
            .Where(c => !body.Contains(c.RecordOffset))
            .Select(c => c.Name)
            .Distinct()
            .Order()];

        Assert.Equal(["CHARGE_UP_TIME_MS", "SPIN_UP_TIME_MS"], sheetOnly);
        Assert.DoesNotContain((short)0x04c, body);
        Assert.DoesNotContain((short)0x054, body);
    }

    /// <summary>
    /// Every named constant in <see cref="WeaponListLayouts"/> that predates this lane still has
    /// its old value, and the loader puts its datasheet column on exactly that offset. This is the
    /// "must agree, or report the disagreement" gate: docs/99 and docs/107 bound these the hard
    /// way, through getters and consumers, and the loader is a second, independent route to the
    /// same answers.
    /// </summary>
    [Theory]
    [InlineData("REFIRE_TIME_MS", WeaponListLayouts.FireModeRefireTime)]
    [InlineData("AUTO_FIRE_TIME_MS", WeaponListLayouts.FireModeAutoFireTime)]
    [InlineData("RANGE", WeaponListLayouts.FireModeRange)]
    [InlineData("RELOAD_TIME_MS", WeaponListLayouts.FireModeReloadTime)]
    [InlineData("RELOAD_CHAMBER_TIME_MS", WeaponListLayouts.FireModeReloadChamberTime)]
    [InlineData("RELOAD_AMMO_FILL_TIME_MS", WeaponListLayouts.FireModeReloadAmmoFillTime)]
    [InlineData("RELOAD_LOOP_START_TIME_MS", WeaponListLayouts.FireModeReloadLoopStartTime)]
    [InlineData("RELOAD_LOOP_END_TIME_MS", WeaponListLayouts.FireModeReloadLoopEndTime)]
    [InlineData("PELLETS_PER_SHOT", WeaponListLayouts.FireModePelletsPerShot)]
    [InlineData("PELLET_SPREAD", WeaponListLayouts.FireModePelletSpread)]
    [InlineData("COF_RECOIL", WeaponListLayouts.FireModeCofRecoil)]
    [InlineData("COF_SCALAR", WeaponListLayouts.FireModeCofScalar)]
    [InlineData("COF_SCALAR_MOVING", WeaponListLayouts.FireModeCofScalarMoving)]
    [InlineData("COF_SCALAR_JUMPING", WeaponListLayouts.FireModeCofScalarJumping)]
    [InlineData("CYLOF_RECOIL", WeaponListLayouts.FireModeCylofRecoil)]
    [InlineData("CYLOF_SCALAR", WeaponListLayouts.FireModeCylofScalar)]
    [InlineData("CYLOF_SCALAR_MOVING", WeaponListLayouts.FireModeCylofScalarMoving)]
    [InlineData("MOVEMENT_MODIFIER", WeaponListLayouts.FireModeMovementModifier)]
    [InlineData("TURN_MODIFIER", WeaponListLayouts.FireModeTurnModifier)]
    [InlineData("AMMO_SLOT", WeaponListLayouts.FireModeAmmoSlot)]
    [InlineData("DEFAULT_ZOOM", WeaponListLayouts.FireModeDefaultZoom)]
    [InlineData("ARMS_FOV_SCALAR", WeaponListLayouts.FireModeArmsFovScalar)]
    [InlineData("TP_FORCE_CAMERA_OVERRIDES", WeaponListLayouts.FireModeTpForceCameraOverrides)]
    [InlineData("FP_FORCE_CAMERA_OVERRIDES", WeaponListLayouts.FireModeFpForceCameraOverrides)]
    [InlineData("FP_CAMERA_FOV", WeaponListLayouts.FireModeFpCameraFov)]
    [InlineData("FP_CR_CAMERA_FOV", WeaponListLayouts.FireModeFpCrouchedCameraFov)]
    [InlineData("FP_PR_CAMERA_FOV", WeaponListLayouts.FireModeFpProneCameraFov)]
    [InlineData("LOCK_ON_ANGLE", 0x114)]
    [InlineData("LOCK_ON_RADIUS", 0x118)]
    [InlineData("LOCK_ON_RANGE", 0x11c)]
    [InlineData("LOCK_ON_LOSE_TIME_MS", 0x124)]
    [InlineData("SWAY_AMPLITUDE_X", 0x150)]
    [InlineData("SWAY_AMPLITUDE_Y", 0x154)]
    [InlineData("SWAY_PERIOD_X", 0x158)]
    [InlineData("SWAY_PERIOD_Y", 0x15c)]
    [InlineData("SWAY_INITIAL_Y_OFFSET", 0x160)]
    [InlineData("SWAY_CROUCH_SCALAR", 0x164)]
    [InlineData("SWAY_PRONE_SCALAR", 0x168)]
    [InlineData("HEAT_PER_SHOT", 0x144)]
    [InlineData("HEAT_THRESHOLD", 0x148)]
    [InlineData("HEAT_RECOVERY_DELAY_MS", 0x14c)]
    [InlineData("INDIRECT_EFFECT", 0x17c)]
    [InlineData("MELEE_COMPOSITE_EFFECT_ID", 0x190)]
    public void TheLoaderAgreesWithEveryOffsetThisProjectBoundTheHardWay(string column, int offset)
    {
        FireModeColumn[] matches = [.. WeaponListLayouts.FireModeColumns
            .Where(c => c.Name == column && c.Kind != FireModeColumnKind.FlagBit)];

        FireModeColumn only = Assert.Single(matches);
        Assert.Equal(offset, only.RecordOffset);
    }

    /// <summary>
    /// <c>IRON_SIGHTS</c> is <c>rec+0x20</c> bit <c>0x04</c> on both routes: docs/107 §8.6 found it
    /// in the setter <c>FUN_1422935d0:68</c>, and the loader writes it at <c>142227309</c>.
    /// </summary>
    [Fact]
    public void TheIronSightsBitAgreesWithTheSetter()
    {
        FireModeColumn column = WeaponListLayouts.FireModeColumns
            .Single(c => c.Name == "IRON_SIGHTS");

        Assert.Equal(FireModeColumnKind.FlagBit, column.Kind);
        Assert.Equal(WeaponListLayouts.FireModeFlags0, column.RecordOffset);
        Assert.Equal(WeaponListLayouts.FireModeIronSightsFlag, column.FlagBit);
    }

    /// <summary>
    /// The recoil family: <b>seventeen</b> columns, contiguous over <c>rec+0x094..rec+0x0d4</c>
    /// with no other column between them. docs/99 §2.5 said "18 columns"; the loader counts 17,
    /// and docs/99 §2.5b now carries the corrected list.
    /// </summary>
    [Fact]
    public void TheRecoilFamilyIsSeventeenContiguousColumns()
    {
        FireModeColumn[] recoil = [.. WeaponListLayouts.FireModeColumns
            .Where(c => c.Name.StartsWith("RECOIL_", StringComparison.Ordinal))
            .OrderBy(c => c.RecordOffset)];

        Assert.Equal(17, recoil.Length);
        Assert.Equal(0x094, recoil[0].RecordOffset);
        Assert.Equal(0x0d4, recoil[^1].RecordOffset);

        // Fifteen floats and two integers (RECOIL_RECOVERY_DELAY_MS, RECOIL_SHOTS_AT_MIN_MAGNITUDE),
        // and the seventeen fill rec+0x094..rec+0x0d4 with no gap and nothing else in the range.
        Assert.Equal(15, recoil.Count(c => c.Kind == FireModeColumnKind.Float));
        Assert.Equal(
            17,
            WeaponListLayouts.FireModeColumns.Count(
                c => c.Kind != FireModeColumnKind.FlagBit
                     && c.RecordOffset >= 0x094 && c.RecordOffset <= 0x0d4));
        Assert.Equal(2, recoil.Count(c => c.Kind == FireModeColumnKind.Int));

        // Every one of them is a field the wire carries, and every one of them ships 0 - no August
        // sheet supplies a recoil number (docs/99 §2.5b, §10).
        HashSet<short> body = [.. WeaponListLayouts.FireModeBody.Select(f => f.RecordOffset)];
        Assert.All(recoil, c => Assert.Contains(c.RecordOffset, body));
        Assert.All(recoil, c => Assert.DoesNotContain(
            c.RecordOffset, WeaponListLayouts.FireModeUnitScalars));
    }

    // ------------------------------------------------------------------ the TSVs

    private sealed record Row(int LoaderOffset, int RecordOffset, string Width, string Name, string Lea);

    private sealed record FullRow(string Name, int RecordOffset, byte? Bit, string Lea);

    private static IReadOnlyList<Row> ReadTsv(string name)
    {
        List<Row> rows = [];
        foreach (string[] cells in Cells(name))
        {
            rows.Add(new Row(
                Hex(cells[0]), Hex(cells[1]), cells[2], cells[3], cells[5]));
        }

        return rows;
    }

    private static IReadOnlyList<FullRow> ReadFullTsv()
    {
        List<FullRow> rows = [];
        foreach (string[] cells in Cells("firemodes-columns-full.tsv"))
        {
            // src is "OR 0x80" / "AND 0xdf" for a bit, or "MOVSS XMM1" / "MOV 0x0" / "?" otherwise.
            string[] parts = cells[3].Split(' ');
            byte? bit = parts.Length == 2 && parts[0] is "OR" or "AND"
                ? parts[0] == "OR"
                    ? (byte)Hex(parts[1])
                    : (byte)~(byte)Hex(parts[1])
                : null;
            rows.Add(new FullRow(cells[0], cells[1] == "?" ? -1 : Hex(cells[1]), bit, cells[4]));
        }

        return rows;
    }

    private static IEnumerable<string[]> Cells(string name)
    {
        string path = Path.Combine(RepoRoot(), "tools", "data", name);
        Assert.True(File.Exists(path), $"missing pipeline input {path}");

        bool header = true;
        foreach (string line in File.ReadLines(path))
        {
            if (header)
            {
                header = false;
                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            yield return line.Split('\t');
        }
    }

    private static int Hex(string text) =>
        int.Parse(
            text.StartsWith("0x", StringComparison.Ordinal) ? text[2..] : text,
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture);

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Walks up from the test assembly to the directory holding <c>Cranberry.slnx</c>.</summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cranberry.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"No directory holding Cranberry.slnx was found above {AppContext.BaseDirectory}");
    }
}
