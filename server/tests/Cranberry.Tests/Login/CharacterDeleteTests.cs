using Cranberry.Login;
using Cranberry.Protocol;

namespace Cranberry.Tests.Login;

public sealed class CharacterDeleteTests
{
    [Fact]
    public void RequestIsOpcodeAndCharacterId()
    {
        // Observed live 2026-08-28 22:46:48: 09 0110000000000000 (entity 4097).
        CharacterDeleteRequest request = CharacterDeleteRequest.Parse(Convert.FromHexString("0110000000000000"));
        Assert.Equal(0x1001ul, request.EntityKey);
        Assert.Throws<PacketFormatException>(() => CharacterDeleteRequest.Parse(Convert.FromHexString("011000000000000000")));
    }

    [Fact]
    public void SuccessReplyEchoesTheGuidWithStatusOneAndAnEmptyPayload()
    {
        using var writer = new PacketWriter();
        new CharacterDeleteReply(0x1001, CharacterDeleteReply.Success).WriteTo(writer);
        Assert.Equal(Convert.FromHexString("0A" + "0110000000000000" + "01000000" + "00000000"), writer.Written.ToArray());
    }

    [Fact]
    public void RosterRemoveDropsOnlyThatCharacter()
    {
        static CharacterCreatePayload Payload(string name, uint gender) => new(
            EmpireId: 0, HeadId: 0, ProfileId: 0, Gender: gender, Name: name, SkinToneId: 0, HairId: 0,
            RuntimeValue: 0, OperatingSystem: "", PlatformVersion: "", ClientVersion: "", Environment: "");

        var roster = new CharacterRosterStore();
        CharacterEntry first = roster.Create(1, Payload("one", 1));
        CharacterEntry second = roster.Create(1, Payload("two", 2));
        Assert.True(roster.Remove(first.EntityKey));
        Assert.False(roster.Remove(first.EntityKey));
        Assert.Single(roster.Snapshot());
        Assert.True(roster.ContainsAvailable(second.EntityKey, 1));
    }
}
