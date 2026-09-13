using System.Numerics;
using Cranberry.Zone;
using Cranberry.Zone.Combat;

namespace Cranberry.Tests.Zone.Combat;

/// <summary>
/// <b><c>82 0c SwitchFireModeRequest</c> - the August client's ADS packet (docs/107 §2).</b>
/// <para>
/// The owner's three 2026-09-02 sessions carry 145 of these, one on every right-click press and one
/// on every release, and until D189 every byte of them was thrown away as "malformed managed
/// movement". The bytes replayed here are his, verbatim, off
/// <c>captures\wire-20260902-212215.txt</c> - so what is pinned is not a candidate layout but a
/// recording, and a decoder change that breaks it breaks a real client.
/// </para>
/// <para>
/// <b>No reply is asserted, deliberately.</b> The local switch is client-authoritative: the client's
/// own writer <c>FUN_14148c1c0:26</c> calls the local setter <c>FUN_1422935d0</c> first and only
/// builds the packet when that returned 1, and <c>FUN_140b07010</c>'s s2c <c>0x82</c> switch has no
/// <c>0x0c</c> case at all. What the server owes is state, not an answer.
/// </para>
/// </summary>
public sealed class FireModeSwitchTests
{
    /// <summary>The instance the owner drew at 21:24:09.073 in session A - <c>item 10</c>.</summary>
    private const ulong SessionAGuid = 0x3100_0000_0000_000D;

    private const ulong Rifle = 0x3100_0000_0000_0001;
    private const uint ArFifteen = AugustHeldWeapon.ItemDefinitionId;

    /// <summary>
    /// <b>The owner's own ADS press, byte for byte</b> - session A, 21:24:14.409, the gateway header
    /// stripped. A <c>82 1f MultiWeapon</c> carrying one member: <c>82 0c</c> with
    /// <c>fireGroupIndex 0</c>, <c>fireModeIndex 1</c> (aim down) and the trailing byte 0.
    /// </summary>
    [Fact]
    public void TheOwnersRecordedAdsPressDecodesAndIsRecorded()
    {
        byte[] recorded = Convert.FromHexString(
            "82" + "00000000" + "1F"                    // 82 1f MultiWeapon, gameTime 0
            + "01000000" + "11000000"                   // memberCount 1, memberLength 0x11 = 17
            + "82" + "B3308F12" + "0C"                  // 82 0c, the client's own gameTime
            + "0D00000000000031"                        // u64 guid = 0x310000000000000D
            + "00" + "01" + "00");                      // group 0, mode 1 = ADS, trailer 0

        Assert.Equal(6 + 8 + 17, recorded.Length);

        var session = new SessionCombat();
        var results = new List<WeaponArmResult>();
        Handle(session, recorded, results, heldGuid: SessionAGuid);

        Assert.Equal(2, results.Count);
        Assert.Contains("MultiWeapon", results[0].Line, StringComparison.Ordinal);
        Assert.Contains("members=1", results[0].Line, StringComparison.Ordinal);

        Assert.Contains("82 0c SwitchFireModeRequest", results[1].Line, StringComparison.Ordinal);
        Assert.Contains("AIM DOWN SIGHTS", results[1].Line, StringComparison.Ordinal);
        Assert.Contains("group=0 mode=1", results[1].Line, StringComparison.Ordinal);
        Assert.Null(results[1].Reply);                  // there is nothing to answer with
        Assert.Equal(0, session.Undecodable);

        Assert.Equal(1, session.Shooter.FireModeOf(SessionAGuid));
        Assert.Equal(0, session.Shooter.FireGroupOf(SessionAGuid));
    }

    /// <summary>
    /// Press and release, which is what the owner's session B does fourteen and fifteen times: the
    /// tracked mode follows the client exactly, and the switch count rises on both edges.
    /// </summary>
    [Fact]
    public void PressAndReleaseMoveTheTrackedModeBetweenOneAndZero()
    {
        var session = new SessionCombat();
        var results = new List<WeaponArmResult>();

        Handle(session, ShootingPacketBuilder.SwitchFireModeRequest(Rifle, 0, 1), results);
        Assert.Equal(1, session.Shooter.FireModeOf(Rifle));

        Handle(session, ShootingPacketBuilder.SwitchFireModeRequest(Rifle, 0, 0), results);
        Assert.Equal(0, session.Shooter.FireModeOf(Rifle));

        Assert.Equal(2, session.Shooter.FireModeSwitchesOf(Rifle));
    }

    /// <summary>
    /// <c>CRANBERRY_WEAPON_FIREMODE_REPLY=0</c> is the control: the packet is still named in the
    /// log, and nothing at all is recorded.
    /// </summary>
    [Fact]
    public void TheSwitchOffLeavesTheArmWhereWaveTwelveLeftIt()
    {
        var session = new SessionCombat();
        var results = new List<WeaponArmResult>();

        Handle(
            session,
            ShootingPacketBuilder.SwitchFireModeRequest(Rifle, 0, 1),
            results,
            options: CombatOptions.Default with { SwitchFireModeReply = false });

        Assert.Contains("82 0c SwitchFireModeRequest", results[0].Line, StringComparison.Ordinal);
        Assert.Contains("seen, not acted on", results[0].Line, StringComparison.Ordinal);
        Assert.Equal(-1, session.Shooter.FireModeOf(Rifle));        // never declared
    }

    /// <summary>
    /// A request naming an instance that is not in the hand is refused, exactly as a shot from it is
    /// - the same active-hand rule, so a weapon that was switched away cannot keep changing modes.
    /// </summary>
    [Fact]
    public void ARequestForSomethingElseThanTheActiveHandIsRefused()
    {
        var session = new SessionCombat();
        var results = new List<WeaponArmResult>();

        Handle(
            session,
            ShootingPacketBuilder.SwitchFireModeRequest(Rifle + 1, 0, 1),
            results,
            heldGuid: Rifle);

        Assert.Contains("REFUSED", results[0].Line, StringComparison.Ordinal);
        Assert.Equal(-1, session.Shooter.FireModeOf(Rifle + 1));
    }

    /// <summary>
    /// <b>The trailing byte is carried, not asserted.</b> The client's writer fills it from
    /// <c>comp-&gt;vtable+0x70()</c> (<c>FUN_14148c1c0:48</c>), so a non-zero one is a first sighting
    /// and must not be a decode failure. A body of the wrong LENGTH still is.
    /// </summary>
    [Fact]
    public void ANonZeroTrailerIsAcceptedAndAWrongLengthIsNot()
    {
        var session = new SessionCombat();
        var results = new List<WeaponArmResult>();

        Handle(session, ShootingPacketBuilder.SwitchFireModeRequest(Rifle, 0, 1, trailer: 3), results);
        Assert.Contains("u8=3", results[0].Line, StringComparison.Ordinal);
        Assert.Equal(1, session.Shooter.FireModeOf(Rifle));

        byte[] short10 =
        [
            ZoneOpcodes.WeaponBase, 0, 0, 0, 0, WeaponBaseDecoder.SubSwitchFireModeRequest,
            0x01, 0, 0, 0, 0, 0, 0, 0x31, 0x00, 0x01,       // the 1087 10-byte body
        ];

        var second = new SessionCombat();
        results.Clear();
        Handle(second, short10, results);

        Assert.Contains("EVIDENCE", results[0].Line, StringComparison.Ordinal);
        Assert.Equal(1, second.Undecodable);
    }

    private static void Handle(
        SessionCombat session,
        byte[] packet,
        List<WeaponArmResult> results,
        ulong heldGuid = 0,
        CombatOptions? options = null) =>
        WeaponFireArm.Handle(
            session,
            packet,
            options ?? CombatOptions.Default,
            ArFifteen,
            heldGuid,
            Vector3.Zero,
            0,
            results);
}
