using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

/// <summary>
/// docs/105 / D193 — the server-owned menu camera table. The two shots from the period trace must
/// survive it byte for byte; the fifteen capture-decoded rows must go out as ClientProtocol_1148
/// with the capture's own values.
/// </summary>
public sealed class MenuViewTests
{
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    [Fact]
    public void TheTwoPeriodShotsComeThroughTheTableUnchanged()
    {
        Assert.True(MenuViewTable.TryResolve(
            "kotkdefault", MenuViewCoverage.All, out StaticViewReply root, out Vector4 rootMark));
        Assert.Equal(
            Convert.FromHexString(
                "E9020000000000000006C266E6FD438F228C430000C0BF000000000000000000000000EE7CFF3E23DBF93E"
                    + "00005C420000000000"),
            Bytes(root.WriteTo));
        Assert.Equal(
            Convert.FromHexString(
                "110A00713DFCC1C335FD4329FC8B430000803F0000000000000000000000000000803F010000"),
            Bytes(w => new UpdateLocation(rootMark, new Vector4(0, 0, 0, 1), Apply: true).WriteTo(w)));

        Assert.True(MenuViewTable.TryResolve(
            "kotkgamemodes", MenuViewCoverage.All, out StaticViewReply play, out Vector4 playMark));
        Assert.Equal(
            Convert.FromHexString(
                "E90200000000000000C8C1CDEC004400C083433333B3BF00000000000000000000000085EBD13EA4701DBF"
                    + "00006C420000000000"),
            Bytes(play.WriteTo));
        Assert.Equal(
            Convert.FromHexString(
                "110A0014AE9541E1FAFC439A598C430000803F0000000000000000000000000000803F010000"),
            Bytes(w => new UpdateLocation(playMark, new Vector4(0, 0, 0, 1), Apply: true).WriteTo(w)));
    }

    [Fact]
    public void TheProvenShotsAnswerAtEveryCoverageExceptOff()
    {
        foreach (string name in MenuViewTable.ReferenceNames)
        {
            Assert.True(MenuViewTable.TryResolve(name, MenuViewCoverage.Reference, out _, out _));
            Assert.True(MenuViewTable.TryResolve(name, MenuViewCoverage.All, out _, out _));
            Assert.False(MenuViewTable.TryResolve(name, MenuViewCoverage.Off, out _, out _));
        }
    }

    [Fact]
    public void ReferenceCoverageIsExactlyThePreD193Behaviour()
    {
        // The one-word revert has to be a true revert, not "nearly": in reference mode the two
        // proven names answer and NOTHING else does.
        foreach (string name in MenuViewTable.AnsweredNames.Except(MenuViewTable.ReferenceNames))
        {
            Assert.False(MenuViewTable.TryResolve(name, MenuViewCoverage.Reference, out _, out _));
        }
    }

    [Fact]
    public void EveryAnsweredShotIsAFiftyTwoByteReplyWithTheCapturesInvariants()
    {
        foreach (string name in MenuViewTable.AnsweredNames)
        {
            Assert.True(
                MenuViewTable.TryResolve(name, MenuViewCoverage.All, out StaticViewReply camera, out Vector4 mark),
                name);

            byte[] bytes = Bytes(camera.WriteTo);
            Assert.Equal(52, bytes.Length);
            Assert.Equal(ZoneOpcodes.StaticViewBase, bytes[0]);
            Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(1)));
            Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(3)));   // result = success
            Assert.Equal(0, bytes[51]);                                                 // hideSubject = 0

            // Every reply in the 2026-08-22 admin capture has these three at zero — the eye IS the
            // target, so a non-zero distance would swing the camera off the authored spot.
            Assert.Equal(name == "kotkappearancegearback" ? -MathF.PI : 0f, camera.YawOffset);
            Assert.Equal(0f, camera.Pitch);
            Assert.Equal(0f, camera.Distance);

            // and every mark is a w = 1 point on the LoginZone apron.
            Assert.Equal(1f, mark.W);
            Assert.InRange(mark.Y, 505f, 517f);

            // The paired UpdateLocation is identity-rotation with apply = 1 (trailer 01 00 00).
            byte[] location = Bytes(
                w => new UpdateLocation(mark, new Vector4(0, 0, 0, 1), Apply: true).WriteTo(w));
            Assert.Equal(38, location.Length);
            Assert.Equal("010000", Convert.ToHexString(location.AsSpan(35)).ToLowerInvariant());
        }
    }

    [Fact]
    public void EveryCharacterScreenStandsTheActorBesideTheClientsOwnTractor()
    {
        // Cross-check between two independent sources: the friend capture's marks and the client's
        // own LoginZone.zone. Common_Props_SemiVolvo_Body.adr sits at (22.291, 505.979, 282.392);
        // the retail footage puts that tractor behind the character on CHARACTER / APPEARANCE /
        // WEAPONS. Every captured mark must therefore be within a few metres of it.
        const float truckX = 22.291f;
        const float truckZ = 282.392f;

        foreach (string name in MenuViewTable.AnsweredNames)
        {
            if (name is "kotkdefault")
            {
                continue;   // the only shot that stands at the helipad lobby mark
            }

            Assert.True(MenuViewTable.TryResolve(name, MenuViewCoverage.All, out _, out Vector4 mark), name);
            float dx = mark.X - truckX;
            float dz = mark.Z - truckZ;
            Assert.InRange(MathF.Sqrt((dx * dx) + (dz * dz)), 3.5f, 4.5f);
        }
    }

    [Fact]
    public void TheGearCloseUpsCarryTheCapturesFocusAreaAndTheOthersCarryZero()
    {
        // The eleventh word is a field, not padding: the capture's gear screens carry 1, 2, 4, 5.
        (string View, uint Focus)[] expected =
        [
            ("kotkdefault", 0u),
            ("kotkgamemodes", 0u),
            ("kotkcharacter", 1u),
            ("kotkappearance", 1u),
            ("kotkappearancegear", 1u),
            ("kotkappearancegearchest", 2u),
            ("kotkappearancegearbodyarmor", 2u),
            ("kotkappearancegearhands", 4u),
            ("kotkappearancegearlegs", 5u),
            ("kotkappearancegearfeet", 5u),
            ("kotkappearanceweaponsguns", 0u),
            ("kotkappearancevehiclesatv", 0u),
            ("kotkcrates", 1u),
            ("kotkgrinder", 1u),
        ];

        foreach ((string view, uint focus) in expected)
        {
            Assert.True(MenuViewTable.TryResolve(view, MenuViewCoverage.All, out StaticViewReply camera, out _), view);
            Assert.Equal(focus, camera.FocusArea);
            byte[] bytes = Bytes(camera.WriteTo);
            Assert.Equal(focus, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(47)));
        }
    }

    [Fact]
    public void EmotePreviewUsesLeftSideWeaponAndSkinFramingWithAnimationEnabled()
    {
        Assert.True(MenuViewTable.TryResolve("kotkappearancegear", MenuViewCoverage.All,
            out StaticViewReply apparel, out Vector4 apparelMark));
        Assert.True(MenuViewTable.TryResolve("kotkappearanceweapons", MenuViewCoverage.All,
            out StaticViewReply weapons, out Vector4 weaponMark));
        Assert.True(MenuViewTable.TryResolve("kotkappearanceemotes", MenuViewCoverage.All,
            out StaticViewReply emotes, out Vector4 emoteMark));
        Assert.Equal(apparelMark, emoteMark);
        Assert.Equal(weaponMark, emoteMark);
        Assert.Equal(apparel with { FocusArea = 0 }, emotes);
        Assert.Equal(weapons with { FocusArea = 0 }, emotes);
        Assert.Equal(1u, apparel.FocusArea);
        byte[] packet = Bytes(emotes.WriteTo);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(47)));
    }

    [Fact]
    public void TheCapturedCharacterAndAppearanceShotsCarryTheCapturesOwnValues()
    {
        Assert.True(MenuViewTable.TryResolve(
            "kotkcharacter", MenuViewCoverage.All, out StaticViewReply character, out Vector4 mark));
        Assert.Equal(16.97f, character.TargetX);
        Assert.Equal(507.00f, character.TargetY);
        Assert.Equal(278.60f, character.TargetZ);
        Assert.Equal(-2.5f, character.Heading);
        Assert.Equal(0.445f, character.AimYawOffset);
        Assert.Equal(0.310f, character.AimPitchOffset);
        Assert.Equal(53.2f, character.Fov);
        Assert.Equal(new Vector4(18.71f, 505.96f, 280.70f, 1f), mark);

        Assert.True(MenuViewTable.TryResolve(
            "kotkappearance", MenuViewCoverage.All, out StaticViewReply appearance, out _));
        Assert.Equal(17.63f, appearance.TargetX);
        Assert.Equal(-2.25f, appearance.Heading);
        Assert.Equal(54.5f, appearance.Fov);

        // kotkappearancefte is not in the capture: it takes its parent APPEARANCE shot, byte for byte.
        Assert.True(MenuViewTable.TryResolve(
            "kotkappearancefte", MenuViewCoverage.All, out StaticViewReply fte, out Vector4 fteMark));
        Assert.Equal(Bytes(appearance.WriteTo), Bytes(fte.WriteTo));
        MenuViewTable.TryResolve("kotkappearance", MenuViewCoverage.All, out _, out Vector4 appearanceMark);
        Assert.Equal(appearanceMark, fteMark);
    }

    [Fact]
    public void ThePlayChildrenReuseTheProvenOverheadShotVerbatim()
    {
        Assert.True(MenuViewTable.TryResolve(
            "kotkgamemodes", MenuViewCoverage.All, out StaticViewReply proven, out Vector4 provenMark));
        byte[] provenBytes = Bytes(proven.WriteTo);

        foreach (string name in new[] { "kotkbrduos", "kotkbrfives", "kotkevents" })
        {
            Assert.True(MenuViewTable.TryResolve(
                name, MenuViewCoverage.All, out StaticViewReply camera, out Vector4 mark), name);
            Assert.Equal(provenBytes, Bytes(camera.WriteTo));
            Assert.Equal(provenMark, mark);
        }
    }

    [Fact]
    public void TheDeliberatelyInheritedScreensAreNeverAnswered()
    {
        foreach (string name in MenuViewTable.InheritNames)
        {
            Assert.False(MenuViewTable.TryResolve(name, MenuViewCoverage.All, out _, out _));
        }

        Assert.False(MenuViewTable.TryResolve("kotknosuchview", MenuViewCoverage.All, out _, out _));
    }

    [Fact]
    public void EveryStaticViewNameInTheClientsOwnMenuItemSheetIsAccountedFor()
    {
        // The client's MenuItem.txt column STATIC_VIEW, verbatim (rows 2, 3, 4, 5, 7, 8, 11, 12, 13,
        // 26, 27, 28, 29, 30, 31, 32, 33, 45), plus kotkappearancefte from the August wire. Every
        // one is either answered or a named inherit — a name that is neither is an omission, which
        // is the failure this test exists for.
        string[] sheet =
        [
            "kotkdefault", "kotksettings", "kotkgamemodes", "kotktwitch", "kotkcharacter",
            "kotkcrates", "kotkbrduos", "kotkbrfives", "kotkevents", "kotkappearance",
            "kotkgrinder", "kotkstats", "kotkappearancegear", "kotkappearanceweapons",
            "kotkappearancevehiclesatv", "kotkappearanceemotes", "kotkappearancefte",
        ];

        foreach (string name in sheet)
        {
            bool answered = MenuViewTable.TryResolve(name, MenuViewCoverage.All, out _, out _);
            Assert.True(
                answered || MenuViewTable.InheritNames.Contains(name),
                $"'{name}' is in the client's own MenuItem.txt but is neither answered nor a named inherit");
        }
    }

    [Fact]
    public void TheEnvironmentSwitchReadsAllReferenceAndOff()
    {
        Assert.Equal(MenuViewCoverage.All, MenuViewOptions.Default.Coverage);
        Assert.Equal(MenuViewCoverage.All, Read(null));
        Assert.Equal(MenuViewCoverage.All, Read("all"));
        Assert.Equal(MenuViewCoverage.Reference, Read("reference"));
        Assert.Equal(MenuViewCoverage.Reference, Read("REF"));
        Assert.Equal(MenuViewCoverage.Off, Read("0"));
        Assert.Equal(MenuViewCoverage.Off, Read("off"));
        Assert.Equal(MenuViewCoverage.All, Read("nonsense"));   // never fails a boot

        static MenuViewCoverage Read(string? value)
        {
            string? saved = System.Environment.GetEnvironmentVariable(MenuViewOptions.CoverageVariable);
            try
            {
                System.Environment.SetEnvironmentVariable(MenuViewOptions.CoverageVariable, value);
                return MenuViewOptions.FromEnvironment().Coverage;
            }
            finally
            {
                System.Environment.SetEnvironmentVariable(MenuViewOptions.CoverageVariable, saved);
            }
        }
    }
}
