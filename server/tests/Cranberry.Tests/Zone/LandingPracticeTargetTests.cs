using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Combat;

namespace Cranberry.Tests.Zone;

public partial class ZoneIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParachuteLandingCreatesAHumanPracticeActorOnlyWhenExplicitlyEnabled(bool diagnosticTarget)
    {
        const ulong chuteGuid = 0x2001;
        var (service, connection, recorder) = Admit(new ZoneOptions
        {
            Combat = diagnosticTarget
                ? CombatOptions.Default with { PracticeTarget = true }
                : CombatOptions.Default,
            GiveStarterWeapon = false,
            GroundLootRadius = 64f,
            SendDoors = false,
            SendVehicles = false,
        });

        ArmMountedParachute(connection, chuteGuid);
        var movement = (SessionMovementState)connection.Tag!.GetType()
            .GetProperty("Movement")!.GetValue(connection.Tag)!;
        movement.RegisterManagedEntity(2, chuteGuid);
        Assert.True(movement.TryApplyManaged(ClientManagedMovementUpdate.Parse(Convert.FromHexString(
            "9008FF1F2F2F1F00002511BDDA021C7E189DB73B0000000000000000000000002203220322032203000000000000000000")),
            out _));

        int beforeLanding = SentCount(recorder);
        SendVehicleDismiss(service, connection);
        byte[][] landing = Sent(recorder, beforeLanding);

        // Chute cleanup and pose handoff still run; never delete the local player to hide a body.
        byte[] removal = Assert.Single(landing, packet => packet.Length > 10
            && packet[1] == RemovePlayer.Opcode && packet[2] == RemovePlayer.SubOpcode);
        Assert.Equal(chuteGuid, BitConverter.ToUInt64(removal, 3));
        Assert.Equal(0, movement.ManagedEntityCount);
        Assert.True(movement.AwaitingPostDismountPose);

        byte[][] humanActors = [.. landing.Where(packet => packet.Length > 9
            && packet[1] == ZoneOpcodes.AddLightweightNpc
            && BitConverter.ToUInt64(packet, 2) == PracticeTargetPack.DefaultWorldGuidBase)];
        if (diagnosticTarget)
        {
            byte[] human = Assert.Single(humanActors);
            var reader = new PacketReader(human.AsSpan(10));
            byte first = reader.ReadByte();
            uint packed = first;
            for (int index = 1; index <= (first & 3); index++)
                packed |= (uint)reader.ReadByte() << (index * 8);
            Assert.Equal(PracticeTargetPack.DefaultTransientIdBase, packed >> 2);
            _ = reader.ReadString();
            _ = reader.ReadUInt32();
            _ = reader.ReadByte();
            Assert.Equal(PracticeTargetPack.DefaultModelId, reader.ReadUInt32());
        }
        else
        {
            Assert.Empty(humanActors);
        }

        int afterLanding = SentCount(recorder);
        SendVehicleDismiss(service, connection);
        Assert.Empty(Sent(recorder, afterLanding));
    }
}
