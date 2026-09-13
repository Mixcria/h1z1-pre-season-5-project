using Cranberry.Login;

namespace Cranberry.Tests.Login;

public sealed class CharacterNamePolicyTests
{
    [Theory]
    [InlineData("Berry", "Berry")]
    [InlineData("  Cranberry7  ", "Cranberry7")]
    [InlineData("sam", "sam")]
    public void ValidNamesAreNormalized(string candidate, string expected)
    {
        Assert.True(CharacterNamePolicy.TryNormalize(candidate, out string normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("7berry")]
    [InlineData("berry_name")]
    [InlineData("berry name")]
    [InlineData("CranberryCharacterName1")]
    public void InvalidNamesAreRejected(string candidate) =>
        Assert.False(CharacterNamePolicy.TryNormalize(candidate, out _));

    [Fact]
    public void UniqueCreationIsAtomicAndCaseInsensitive()
    {
        var roster = new CharacterRosterStore();
        CharacterCreatePayload payload = Payload("Berry");

        Assert.True(roster.TryCreateUnique(1, payload, out CharacterEntry first));
        Assert.False(roster.TryCreateUnique(1, Payload("bErRy"), out _));
        Assert.True(roster.ContainsName("BERRY"));
        Assert.Single(roster.Snapshot());
        Assert.Equal("Berry", first.Name);
    }

    private static CharacterCreatePayload Payload(string name) => new(
        EmpireId: 2,
        HeadId: 3,
        ProfileId: 270,
        Gender: 2,
        Name: name,
        SkinToneId: 664,
        HairId: 2,
        RuntimeValue: 2,
        OperatingSystem: "Windows 10 Home",
        PlatformVersion: "6.2",
        ClientVersion: "0.0.118.208059",
        Environment: "Live");
}
