using System.Numerics;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Combat;

// Reuse the live gateway fixture so these assertions include packet routing and player authority,
// as well as the practice-target arbitration path.
public sealed partial class LivePlayerCombatTests
{
    private sealed record FeedbackVictim(ulong Guid, Func<uint> Health);

    private static FeedbackVictim AddFeedbackVictim(Fixture fixture, SoeConnection shooter,
        bool practice, uint helmetItem = 0, uint armourItem = 0)
    {
        if (practice)
        {
            ulong guid = fixture.SpawnPlayerDummy(shooter);
            var dummy = Get<SessionCombat>(shooter.Tag!, "Combat").Targets.Find(guid)!;
            dummy.Armour = default;
            dummy.HelmetItemId = helmetItem;
            dummy.BodyArmourItemId = armourItem;
            if (helmetItem != 0) dummy.Armour.WearingHelmet(helmetItem);
            if (armourItem != 0) dummy.Armour.Wearing(armourItem);
            return new(guid, () => (uint)dummy.Health);
        }

        var victim = fixture.Add(2, new(5, 0, 0));
        var inventory = new PlayerInventory(2, Get<LootWorld>(victim.Tag!, "Loot").NextItemGuid,
            new InventoryOptions { StarterOutfit = [] });
        inventory.Bootstrap();
        foreach (uint itemId in new[] { helmetItem, armourItem }.Where(itemId => itemId != 0))
        {
            inventory.TryPickUp(itemId, 1, out var protection);
            Assert.NotNull(protection);
            Assert.Contains(inventory.EquipmentSlots.Values, item => item.Guid == protection.Guid);
        }
        Set(victim.Tag!, "Inventory", inventory);
        return new(2, () => fixture.Health(victim));
    }

    private static bool IsHitFeedback(byte[] packet) =>
        packet.Length >= 3 && packet[1] == 0x1a && packet[2] is 0x10 or 0x1c;

    private static bool IsHitFeedbackAudio(byte[] packet)
    {
        if (packet.Length < 3 || packet[1] != 0xdc || packet[2] != 0x03) return false;
        if (packet.Length < 7 || WireUInt32(packet, 3) != packet.Length - 7) return true;
        string cue = System.Text.Encoding.UTF8.GetString(packet.AsSpan(7));
        return cue.StartsWith("UI_HIT_", StringComparison.OrdinalIgnoreCase)
            || cue.StartsWith("UI_BREAK_ARMOR_", StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertHitFeedback(Fixture fixture, int mark, SoeConnection shooter,
        bool head = false, bool armour = false, bool broken = false, bool killed = false,
        uint damageUnits = 2500)
    {
        var sent = fixture.Recorder.Routed.Skip(mark).ToArray();
        var marker = Assert.Single(sent, record => IsHitFeedback(record.Packet));
        Assert.Same(shooter, marker.Connection);
        Assert.Equal(12, marker.Packet.Length); // Includes the gateway tunnel byte.
        Assert.Equal(0x10, marker.Packet[2]); // The modern HUD consumes WeaponHitFeedback, not ConfirmHit.
        byte flags = (byte)(0x21 | (head ? 0x04 : 0) | (killed ? 0x10 : 0)
            | (armour ? 0x40 : 0) | (broken ? 0x80 : 0));
        Assert.Equal(flags, marker.Packet[7]);
        Assert.Equal(damageUnits, WireUInt32(marker.Packet, 3));
        Assert.Equal(uint.MaxValue, WireUInt32(marker.Packet, 8)); // Native neutral feedback id is -1.

        var audio = Assert.Single(sent, record => IsHitFeedbackAudio(record.Packet));
        Assert.Same(shooter, audio.Connection);
        Assert.Equal(0x05, audio.Packet[0]);
        string expectedCue = (broken ? "UI_BREAK_ARMOR_" : armour ? "UI_HIT_ARMOR_" : "UI_HIT_FLESH_")
            + (head ? "HEAD" : "BODY");
        byte[] expectedUtf8 = System.Text.Encoding.UTF8.GetBytes(expectedCue);
        Assert.Equal((uint)expectedUtf8.Length, WireUInt32(audio.Packet, 3));
        Assert.Equal(7 + expectedUtf8.Length, audio.Packet.Length); // No target guid or trailing NUL.
        Assert.Equal(expectedUtf8, audio.Packet.AsSpan(7).ToArray());
        Assert.True(Array.FindIndex(sent, record => IsHitFeedback(record.Packet))
            < Array.FindIndex(sent, record => IsHitFeedbackAudio(record.Packet)));
    }

    private static void AssertNoHitFeedback(Fixture fixture, int mark) =>
        Assert.DoesNotContain(fixture.Recorder.Routed.Skip(mark), record =>
            IsHitFeedback(record.Packet) || IsHitFeedbackAudio(record.Packet));

    private static void FeedbackShot(Fixture fixture, SoeConnection shooter, ulong targetGuid,
        uint projectile, string location = "SPINE", uint weaponItem = 2425,
        CombatOptions? combatOptions = null)
    {
        long now = projectile * 1000;
        void Weapon(byte[] packet)
        {
            object state = shooter.Tag!;
            WeaponFireArm.Handle(Get<SessionCombat>(state, "Combat"), packet, combatOptions ?? CombatOptions.Default,
                weaponItem, Get<SessionMovementState>(state, "Movement").Player!.Position!.Value, now,
                Get<List<WeaponArmResult>>(state, "WeaponArmResults"));
            Call(fixture.Service, "DrainCombatArm", shooter, state);
        }
        int mark = fixture.Recorder.Routed.Count;
        Weapon(ShootingPacketBuilder.Fire(0x3100000000000001, 0, 0, 0, [projectile]));
        AssertNoHitFeedback(fixture, mark); // Pulling the trigger alone never confirms a hit.
        Weapon(ShootingPacketBuilder.HitReport(projectile, targetGuid, location));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HitFeedbackForBareFleshReachesOnlyTheShooter(bool practice)
    {
        using var f = new Fixture();
        var shooter = f.Add(1, Vector3.Zero);
        var victim = AddFeedbackVictim(f, shooter, practice);
        f.Add(3, new(7, 0, 0));
        f.SeeEveryone();
        int mark = f.Recorder.Routed.Count;

        FeedbackShot(f, shooter, victim.Guid, 1);

        Assert.Equal(7500u, victim.Health());
        AssertHitFeedback(f, mark, shooter);
    }

    [Theory]
    [InlineData(false, "HEAD")]
    [InlineData(true, "HEAD")]
    [InlineData(false, "GLASSES")]
    [InlineData(true, "GLASSES")]
    [InlineData(false, "neck")]
    [InlineData(true, "neck")]
    public void HitFeedbackChangesFromHelmetToBareHeadAfterTheHelmetBreaks(bool practice, string location)
    {
        using var f = new Fixture();
        var shooter = f.Add(1, Vector3.Zero);
        var victim = AddFeedbackVictim(f, shooter, practice, helmetItem: 2168);
        f.Add(3, new(7, 0, 0)); // Keep live combat active after the victim dies.
        f.SeeEveryone();
        int mark = f.Recorder.Routed.Count;

        FeedbackShot(f, shooter, victim.Guid, 1, location);

        Assert.Equal(10000u, victim.Health());
        AssertHitFeedback(f, mark, shooter, head: true, armour: true, broken: true, damageUnits: 0);
        mark = f.Recorder.Routed.Count;

        FeedbackShot(f, shooter, victim.Guid, 2, location);

        Assert.Equal(0u, victim.Health());
        AssertHitFeedback(f, mark, shooter, head: true, killed: true, damageUnits: 10000);
        mark = f.Recorder.Routed.Count;
        FeedbackShot(f, shooter, victim.Guid, 3, location);
        AssertNoHitFeedback(f, mark); // A new projectile striking the corpse is not a hit.
    }

    [Theory]
    [InlineData(false, 2204u, 1)]
    [InlineData(true, 2204u, 1)]
    [InlineData(false, 2205u, 1)]
    [InlineData(true, 2205u, 1)]
    [InlineData(false, 2271u, 2)]
    [InlineData(true, 2271u, 2)]
    public void HitFeedbackChangesFromMakeshiftOrLaminatedArmourToFleshAfterItsLastAbsorb(
        bool practice, uint armourItem, int absorbs)
    {
        using var f = new Fixture();
        var shooter = f.Add(1, Vector3.Zero);
        var victim = AddFeedbackVictim(f, shooter, practice, armourItem: armourItem);
        f.Add(3, new(7, 0, 0));
        f.SeeEveryone();

        for (uint projectile = 1; projectile <= absorbs; projectile++)
        {
            int mark = f.Recorder.Routed.Count;
            FeedbackShot(f, shooter, victim.Guid, projectile);
            Assert.Equal(10000u, victim.Health());
            AssertHitFeedback(f, mark, shooter, armour: true, broken: projectile == absorbs, damageUnits: 0);
        }

        int fleshMark = f.Recorder.Routed.Count;
        FeedbackShot(f, shooter, victim.Guid, (uint)absorbs + 1);
        Assert.Equal(7500u, victim.Health());
        AssertHitFeedback(f, fleshMark, shooter);
    }

    [Theory]
    [InlineData(false, "HEAD")]
    [InlineData(true, "HEAD")]
    [InlineData(false, "GLASSES")]
    [InlineData(true, "GLASSES")]
    public void HitFeedbackForShotgunHeadPelletsShowsHelmetContactWithoutChangingDamageOrBreakingIt(
        bool practice, string location)
    {
        using var f = new Fixture();
        var shooter = f.Add(1, Vector3.Zero);
        var victim = AddFeedbackVictim(f, shooter, practice, helmetItem: 2168);
        f.Add(3, new(7, 0, 0));
        if (practice)
        {
            var dummy = Get<SessionCombat>(shooter.Tag!, "Combat").Targets.Find(victim.Guid)!;
            f.Move(shooter, dummy.Position - new Vector3(4, 0, 0));
        }
        f.SeeEveryone();
        int mark = f.Recorder.Routed.Count;

        FeedbackShot(f, shooter, victim.Guid, 1, location, weaponItem: 1374);

        Assert.Equal(9294u, victim.Health());
        AssertHitFeedback(f, mark, shooter, head: true, armour: true, damageUnits: 706);
        if (practice)
        {
            var dummy = Get<SessionCombat>(shooter.Tag!, "Combat").Targets.Find(victim.Guid)!;
            Assert.True(dummy.Armour.HelmetIntact);
            dummy.HelmetItemId = 0;
        }
        else
        {
            var inventory = Get<PlayerInventory>(f.Connections.Single(c => Get<ulong>(c.Tag!, "Guid") == 2).Tag!, "Inventory");
            var helmet = Assert.Single(inventory.EquipmentSlots.Values, item => item.DefinitionId == 2168);
            inventory.RemoveUnits(helmet.Guid, 0);
        }

        mark = f.Recorder.Routed.Count;
        FeedbackShot(f, shooter, victim.Guid, 2, location, weaponItem: 1374);

        Assert.Equal(8588u, victim.Health());
        AssertHitFeedback(f, mark, shooter, head: true, damageUnits: 706);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HitFeedbackForHuntingRifleHelmetContactRemainsYellowOnTheLethalHeadshot(bool practice)
    {
        using var f = new Fixture();
        var shooter = f.Add(1, Vector3.Zero);
        var victim = AddFeedbackVictim(f, shooter, practice, helmetItem: 2168);
        f.Add(3, new(7, 0, 0));
        f.SeeEveryone();
        int mark = f.Recorder.Routed.Count;

        FeedbackShot(f, shooter, victim.Guid, 1, "HEAD", weaponItem: 1373);

        Assert.Equal(0u, victim.Health());
        AssertHitFeedback(f, mark, shooter, head: true, armour: true, killed: true, damageUnits: 10000);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HitFeedbackAudioFollowsTheStruckSurfaceWhenBothHelmetAndBodyArmourAreEquipped(bool practice)
    {
        using var f = new Fixture();
        var shooter = f.Add(1, Vector3.Zero);
        var victim = AddFeedbackVictim(f, shooter, practice, helmetItem: 2168, armourItem: 2271);
        f.Add(3, new(7, 0, 0));
        f.SeeEveryone();
        int mark = f.Recorder.Routed.Count;

        FeedbackShot(f, shooter, victim.Guid, 1);
        AssertHitFeedback(f, mark, shooter, armour: true, damageUnits: 0);
        Assert.Equal(10000u, victim.Health());
        mark = f.Recorder.Routed.Count;

        FeedbackShot(f, shooter, victim.Guid, 2, "HEAD");
        AssertHitFeedback(f, mark, shooter, head: true, armour: true, broken: true, damageUnits: 0);
        Assert.Equal(10000u, victim.Health());
        mark = f.Recorder.Routed.Count;

        FeedbackShot(f, shooter, victim.Guid, 3);
        AssertHitFeedback(f, mark, shooter, armour: true, broken: true, damageUnits: 0);
        Assert.Equal(10000u, victim.Health());
        mark = f.Recorder.Routed.Count;

        FeedbackShot(f, shooter, victim.Guid, 4);
        AssertHitFeedback(f, mark, shooter);
        Assert.Equal(7500u, victim.Health());
        mark = f.Recorder.Routed.Count;

        FeedbackShot(f, shooter, victim.Guid, 5, "HEAD");
        AssertHitFeedback(f, mark, shooter, head: true, killed: true, damageUnits: 10000);
        Assert.Equal(0u, victim.Health());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HitFeedbackSwitchDisablesBothMarkerAndAudioWithoutDisablingDamage(bool practice)
    {
        using var f = new Fixture();
        var combat = CombatOptions.Default with { SendHitMarker = false };
        var optionsField = typeof(ZoneService).GetField("_options",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var options = (ZoneOptions)optionsField.GetValue(f.Service)!;
        optionsField.SetValue(f.Service, options with { Combat = combat });
        var shooter = f.Add(1, Vector3.Zero);
        var victim = AddFeedbackVictim(f, shooter, practice);
        f.Add(3, new(7, 0, 0));
        f.SeeEveryone();
        int mark = f.Recorder.Routed.Count;

        FeedbackShot(f, shooter, victim.Guid, 1, combatOptions: combat);

        Assert.Equal(7500u, victim.Health());
        AssertNoHitFeedback(f, mark);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HitFeedbackDoesNotAcknowledgeUnfiredReplayedWorldOrUnknownTargetReports(bool practice)
    {
        using var f = new Fixture();
        var shooter = f.Add(1, Vector3.Zero);
        var victim = AddFeedbackVictim(f, shooter, practice);
        f.Add(3, new(7, 0, 0));
        f.SeeEveryone();
        int mark = f.Recorder.Routed.Count;

        f.Weapon(shooter, ShootingPacketBuilder.HitReport(1, victim.Guid, "SPINE"), 1000);
        AssertNoHitFeedback(f, mark);
        Assert.Equal(10000u, victim.Health());

        FeedbackShot(f, shooter, victim.Guid, 1);
        AssertHitFeedback(f, mark, shooter);
        mark = f.Recorder.Routed.Count;
        f.Weapon(shooter, ShootingPacketBuilder.HitReport(1, victim.Guid, "SPINE"), 1000);
        FeedbackShot(f, shooter, 0, 2);
        FeedbackShot(f, shooter, 0x9999999999999999, 3);
        f.Weapon(shooter, ShootingPacketBuilder.Fire(0x3100000000000001, 0, 0, 0, [4]), 4000);
        f.Weapon(shooter, ShootingPacketBuilder.HitReport(4, victim.Guid, "SPINE"), 64000);

        AssertNoHitFeedback(f, mark);
        Assert.Equal(7500u, victim.Health());
    }

    [Theory]
    [InlineData("unseen")]
    [InlineData("other match")]
    [InlineData("out of range")]
    [InlineData("invulnerable")]
    [InlineData("self")]
    [InlineData("dead shooter")]
    public void HitFeedbackDoesNotAcknowledgeRefusedLivePlayerHits(string refusal)
    {
        using var f = new Fixture();
        var shooter = f.Add(1, Vector3.Zero);
        var victim = f.Add(2, new(5, 0, 0), matchId: refusal == "other match" ? 2UL : 1UL);
        f.Add(3, new(7, 0, 0));
        if (refusal != "unseen") f.SeeEveryone();
        if (refusal == "out of range") f.Move(victim, new(10000, 0, 0));
        if (refusal == "invulnerable") Get<ConsoleSession>(victim.Tag!, "DevConsole").Invulnerable = true;
        if (refusal == "dead shooter") f.Service.ForTest(shooter).Damage(10000, DamageCause.ToxicGas);
        int mark = f.Recorder.Routed.Count;

        FeedbackShot(f, shooter, refusal == "self" ? 1UL : 2UL, 1);

        AssertNoHitFeedback(f, mark);
        Assert.Equal(10000u, f.Health(victim));
    }

    [Fact]
    public void HitFeedbackDoesNotAcknowledgeAnOutOfRangeDummy()
    {
        using var f = new Fixture();
        var shooter = f.Add(1, Vector3.Zero);
        var victim = AddFeedbackVictim(f, shooter, practice: true);
        f.Move(shooter, new(10000, 0, 0));
        int mark = f.Recorder.Routed.Count;

        FeedbackShot(f, shooter, victim.Guid, 1);

        AssertNoHitFeedback(f, mark);
        Assert.Equal(10000u, victim.Health());
    }
}
