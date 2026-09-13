using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Tests.Zone.Combat;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Parity;

/// <summary>
/// Crosses the final August definition writer with combat and live-inventory clocks.
/// This verifies consistency, not the fidelity of a tuning value to protocol 1315.
/// </summary>
public sealed class ParityWeaponAuthorityClockTests
{
    public static IEnumerable<object[]> Modes()
    {
        foreach (WeaponTableSource source in new[] { WeaponTableSource.Generated, WeaponTableSource.Captured })
        foreach (uint item in new uint[] { 2425, 2229, 4033 }) // AR, AK, AK cosmetic without its own sheet row
        foreach (byte mode in new byte[] { 0, 1 })
            yield return [source, item, mode];
    }

    public static IEnumerable<object[]> Reloads()
    {
        foreach (object[] mode in Modes())
        foreach (int magazine in new[] { 0, 5 })
            yield return [.. mode, magazine];
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void FinalSerializedRefireClockGovernsTheSelectedModeAndAmmo(
        WeaponTableSource source, uint itemId, byte modeIndex)
    {
        int refireMs = SerializedClock(source, itemId, modeIndex, WeaponListLayouts.FireModeRefireTime);
        CombatOptions options = HostCombatProfile(source);
        int gate = Math.Max(options.RefireFloorMs, refireMs);
        int tolerance = Math.Clamp(options.RefireJitterMs, 0, Math.Min(16, gate / 4));
        const ulong gun = 101;
        var shooter = new ShooterCombatState();
        shooter.DeclareWeapon(gun, itemId, 2);
        Assert.True(shooter.SelectFireMode(gun, 0, modeIndex));

        FireResult first = shooter.Fire(new(gun, 0, 0, 0, [1]), itemId, 1000, options);
        Assert.Equal(FireVerdict.Accepted, first.Verdict);
        Assert.Equal(gate, first.GateMs);
        FireResult early = shooter.Fire(new(gun, 0, 0, 0, [2]), itemId, 1000 + gate - tolerance - 1, options);
        Assert.Equal(FireVerdict.RateOfFire, early.Verdict);
        Assert.Equal(1, early.AmmoLeft);
        FireResult due = shooter.Fire(new(gun, 0, 0, 0, [3]), itemId, 1000 + gate - tolerance, options);
        Assert.Equal(FireVerdict.Accepted, due.Verdict);
        Assert.Equal(gate, due.GateMs);
        Assert.Equal(0, due.AmmoLeft);
        Assert.Equal(2, shooter.ShotsFired);
    }

    [Theory]
    [MemberData(nameof(Reloads))]
    public void FinalSerializedReloadDeadlineSurvivesAmmoPickupAndConservesRounds(
        WeaponTableSource source, uint itemId, byte modeIndex, int initialMagazine)
    {
        int reloadMs = SerializedClock(source, itemId, modeIndex, WeaponListLayouts.FireModeReloadTime);
        ulong next = 100;
        var inventory = new PlayerInventory(1, () => ++next);
        inventory.Bootstrap();
        var gun = inventory.CreateInstance(itemId, 1);
        inventory.BindLoadout(gun, SurvivorLoadout.Wheel1, BodySlots.RightHand);
        uint ammoId = AmmoTypes.AmmoItemFor(itemId);
        Assert.NotEqual(0u, ammoId);
        Assert.True(inventory.TryStow(inventory.CreateInstance(ammoId, 40)));
        var ammo = new PlayerAmmoContext(inventory, 1, AmmoOptions.Default);
        var session = new SessionCombat();
        session.Shooter.DeclareWeapon(gun.Guid, itemId, initialMagazine);
        Assert.True(session.Shooter.SelectFireMode(gun.Guid, 0, modeIndex));
        var results = new List<WeaponArmResult>();
        WeaponFireArm.Handle(session, ShootingPacketBuilder.ReloadRequest(gun.Guid),
            HostCombatProfile(source), itemId, gun.Guid, Vector3.Zero, 1000, results, ammo);
        var pending = Assert.IsType<PendingWeaponReload>(Assert.Single(results).ReloadWork);
        Assert.Equal(1000L + reloadMs, pending.DueAtMs);
        Assert.Equal(reloadMs, pending.IntervalMs);
        Assert.False(pending.ShellByShell);

        // A successful reserve grant occurs between request and the advertised deadline.
        Assert.True(inventory.TryStow(inventory.CreateInstance(ammoId, 5)));
        Assert.Same(pending, session.Reload);
        Assert.Equal(1000L + reloadMs, pending.DueAtMs);
        Assert.Null(WeaponFireArm.AdvanceReload(session, pending, pending.DueAtMs - 1, gun.Guid, inventory));
        Assert.Equal(initialMagazine, session.Shooter.AmmoOf(gun.Guid));
        Assert.Equal(45, ammo.Count(ammoId));

        Assert.NotNull(WeaponFireArm.AdvanceReload(session, pending, pending.DueAtMs, gun.Guid, inventory));
        Assert.Equal(RetailBalance.ClipSize(itemId), session.Shooter.AmmoOf(gun.Guid));
        Assert.Equal(initialMagazine + 45, session.Shooter.AmmoOf(gun.Guid) + ammo.Count(ammoId));
        Assert.Null(session.Reload);
        Assert.Null(WeaponFireArm.AdvanceReload(session, pending, pending.DueAtMs + 5000, gun.Guid, inventory));
        Assert.Equal(initialMagazine + 45, session.Shooter.AmmoOf(gun.Guid) + ammo.Count(ammoId));
    }

    private static CombatOptions HostCombatProfile(WeaponTableSource source) =>
        source == WeaponTableSource.Captured
            // src/Cranberry.Host/Program.cs applies these over the configured defaults.
            ? CombatOptions.Default with
            {
                MagazineResync = false, ShippedRefireGate = true,
                ShippedReloadTime = true, RefireJitterMs = 16,
            }
            : CombatOptions.Default;

    private static int SerializedClock(WeaponTableSource source, uint itemId, byte modeIndex, short fieldOffset)
    {
        var weapons = new WeaponSession(WeaponStageOptions.Default with { WeaponTable = source });
        WeaponDefinitionsBlob table = weapons.Blob;
        Assert.True(WeaponItemProfiles.TryGet(itemId, out AugustWeaponFact fact));
        WeaponDefinitionRecord definition = Assert.Single(table.WeaponDefinitions!, row => row.WeaponDefinitionId == fact.WeaponId);
        FireGroupRecord group = Assert.Single(table.FireGroups!, row => row.FireGroupId == definition.FireGroupIds[0]);
        uint modeId = group.FireModeIds![modeIndex];
        Assert.True(weapons.TryCreateWeaponDefinitions(out var packet));

        // Read the final emitted payload after every overlay, not the named record properties:
        // captured Overrides can supersede those properties during serialization.
        int cursor = 4 + table.WeaponDefinitions!.Sum(row => row.Length);
        Assert.Equal(table.FireGroups!.Count, BinaryPrimitives.ReadInt32LittleEndian(packet.Payload.AsSpan(cursor)));
        cursor += 4 + table.FireGroups.Sum(row => row.Length);
        Assert.Equal(table.FireModes!.Count, BinaryPrimitives.ReadInt32LittleEndian(packet.Payload.AsSpan(cursor)));
        cursor += 4;
        foreach (FireModeRecord mode in table.FireModes)
        {
            Assert.Equal(mode.FireModeId, BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.AsSpan(cursor)));
            if (mode.FireModeId == modeId)
            {
                int fieldCursor = cursor + 8;
                foreach (WeaponListField field in WeaponListLayouts.FireModeBody)
                {
                    if (field.RecordOffset == fieldOffset)
                    {
                        Assert.Equal(WeaponListFieldKind.Int16, field.Kind);
                        int value = BinaryPrimitives.ReadInt16LittleEndian(packet.Payload.AsSpan(fieldCursor));
                        Assert.True(value > 0);
                        return value;
                    }
                    fieldCursor += field.Size;
                }
            }
            cursor += mode.Length;
        }
        throw new InvalidOperationException($"No serialized clock at mode {modeId}, offset {fieldOffset:x}.");
    }
}
