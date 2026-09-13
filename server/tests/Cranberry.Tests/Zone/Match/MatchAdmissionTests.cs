using Cranberry.Protocol;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.MatchLobby;

public sealed class MatchAdmissionTests
{
    private static readonly byte[] CapturedPublicRequest = Convert.FromHexString("EC0100000000000000000000000101000000");

    [Fact]
    public void DecodesTheCapturedAugustPublicPlayerRequest()
    {
        Assert.True(PlayerWorldTransferRequest.TryParse(CapturedPublicRequest, out PlayerWorldTransferRequest? request));
        Assert.Equal(new(1, "", 0, 1, 1), request);
        Assert.Equal(new(42, MatchQueueKind.Public, MatchMode.Solo), MatchAdmissionRegistry.Default.Resolve(request, 42));
        Assert.True(MatchAdmissionRegistry.Default.TryGetDefinition(1, out MatchAdmissionDefinition? definition));
        Assert.Equal(13u, definition.GameModeId);
    }

    [Fact]
    public void NeutralFieldsAreDecodedWithoutInventingTheirMeaning()
    {
        using var writer = new PacketWriter();
        writer.WriteByte(0xec);
        writer.WriteUInt32(17);
        writer.WriteString("invite-42");
        writer.WriteUInt32(29);
        writer.WriteByte(0x7e);
        writer.WriteInt32(-1);
        Assert.True(PlayerWorldTransferRequest.TryParse(writer.Written, out PlayerWorldTransferRequest? request));
        Assert.Equal(new(17, "invite-42", 29, 0x7e, -1), request);
        Assert.Equal(MatchAdmissionContext.Unknown, MatchAdmissionRegistry.Default.Resolve(request, 42));
    }

    [Fact]
    public void TruncationAndTrailingBytesCannotMasqueradeAsAValidTransfer()
    {
        for (int length = 0; length < CapturedPublicRequest.Length; length++)
            Assert.False(PlayerWorldTransferRequest.TryParse(CapturedPublicRequest.AsSpan(0, length), out _));
        Assert.False(PlayerWorldTransferRequest.TryParse([.. CapturedPublicRequest, 0], out _));
        byte[] wrongOpcode = [.. CapturedPublicRequest];
        wrongOpcode[0] = 0xed;
        Assert.False(PlayerWorldTransferRequest.TryParse(wrongOpcode, out _));
    }

    [Fact]
    public void UnboundedStringLengthAndInvalidUtf8AreRejected()
    {
        byte[] badCount = [.. CapturedPublicRequest];
        badCount.AsSpan(5, 4).Fill(0xff);
        Assert.False(PlayerWorldTransferRequest.TryParse(badCount, out _));
        using var writer = new PacketWriter();
        writer.WriteByte(0xec);
        writer.WriteUInt32(1);
        writer.WriteUInt32(1);
        writer.WriteByte(0xff);
        writer.WriteUInt32(0);
        writer.WriteByte(1);
        writer.WriteInt32(1);
        Assert.False(PlayerWorldTransferRequest.TryParse(writer.Written, out _));
    }

    [Fact]
    public void PublicDefinitionDoesNotAuthorizeCustomTextOrUnknownFlags()
    {
        var ordinary = new PlayerWorldTransferRequest(1, "", 0, 1, 1);
        PlayerWorldTransferRequest[] unknown =
        [
            ordinary with { WorldId = 2 },
            ordinary with { AdmissionText = "custom-solo" },
            ordinary with { UnknownValue = 1 },
            ordinary with { Flag = 0 },
            ordinary with { Flag = 2 },
            ordinary with { Role = 0 },
            ordinary with { Role = 2 },
            ordinary with { Role = 3 },
        ];
        foreach (PlayerWorldTransferRequest request in unknown)
            Assert.Equal(MatchAdmissionContext.Unknown, MatchAdmissionRegistry.Default.Resolve(request, 42));
        Assert.Equal(MatchAdmissionContext.Unknown, MatchAdmissionRegistry.Default.Resolve(ordinary, 0));
        Assert.Equal(MatchAdmissionContext.Unknown, MatchAdmissionRegistry.Default.Resolve(null, 42));
    }

    [Fact]
    public void HostedAndCustomSoloNeverInheritPublicBountyEligibility()
    {
        foreach (MatchQueueKind queue in new[] { MatchQueueKind.Hosted, MatchQueueKind.Custom })
        {
            var registry = new MatchAdmissionRegistry([new(1, 13, queue, MatchMode.Solo)]);
            MatchAdmissionContext context = registry.Resolve(new(1, "custom", 0, 1, 1), 42);
            Assert.Equal(queue, context.QueueKind);
            Assert.Equal(MatchMode.Solo, context.Mode);
            Assert.False(BountyEligibility.CanBack(context, BountyPhase.Lobby));
        }
    }

    [Fact]
    public void ModeComesFromTheDefinitionRatherThanTheHudId()
    {
        var registry = new MatchAdmissionRegistry([new(1, 13, MatchQueueKind.Public, MatchMode.Duos)]);
        MatchAdmissionContext context = registry.Resolve(new(1, "", 0, 1, 1), 42);
        Assert.Equal(MatchMode.Duos, context.Mode);
        Assert.False(BountyEligibility.IsEligible(context));
    }

    [Fact]
    public void AmbiguousOrInvalidDefinitionsAreConfigurationErrors()
    {
        var solo = new MatchAdmissionDefinition(1, 13, MatchQueueKind.Public, MatchMode.Solo);
        Assert.Throws<ArgumentException>(() => new MatchAdmissionRegistry([solo, solo with { Mode = MatchMode.Duos }]));
        Assert.Throws<ArgumentException>(() => new MatchAdmissionRegistry([solo with { WorldId = 0 }]));
        Assert.Throws<ArgumentException>(() => new MatchAdmissionRegistry([solo with { Mode = MatchMode.Unknown }]));
        var definitions = new List<MatchAdmissionDefinition> { solo };
        var registry = new MatchAdmissionRegistry(definitions);
        definitions.Clear();
        Assert.True(registry.TryGetDefinition(1, out _));
    }
}
