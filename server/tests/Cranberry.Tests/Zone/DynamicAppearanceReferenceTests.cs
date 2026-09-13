using Cranberry.Protocol;
using Cranberry.Zone;

namespace Cranberry.Tests.Zone;

public sealed class DynamicAppearanceReferenceTests
{
    [Theory]
    [InlineData(1u, 1u, "Male")]
    [InlineData(2u, 3u, "Female")]
    public void DefaultOutfitUsesGreyHenleyAndNoRedTint(uint gender, uint head, string meshGender)
    {
        var visuals = CharacterVisuals.FromSelection(gender, head, 1, 664, 270);
        var shirt = Assert.Single(visuals.StarterOutfit, item => item.SlotId == 3);
        Assert.Equal($"Survivor{meshGender}_Chest_Shirt_Henley.adr", shirt.ModelName);
        Assert.True(Cranberry.Zone.Appearance.AugustWornVisuals.TryResolveMesh(3533, gender, "",
            out string physicalShirt, out _));
        Assert.Equal(shirt.ModelName, physicalShirt);
        foreach (var tint in DynamicAppearanceReference.StarterValues.Where(value =>
            value.ShaderParameterGroupId == shirt.ShaderParameterGroupId))
        {
            Assert.Equal(tint.Value.X, tint.Value.Y);
            Assert.Equal(tint.Value.Y, tint.Value.Z);
        }
        Assert.Equal(3533u, Cranberry.Zone.Inventory.SurvivorStarterOutfit.Shirt);
        Assert.Equal(2177u, Cranberry.Zone.Inventory.SurvivorStarterOutfit.Pants);
    }

    [Fact]
    public void EmptyShaderOnlyTableHasThreeExactZeroCounts()
    {
        byte[] payload = DynamicAppearanceReference.BuildShaderOnlyBlob([]);

        Assert.Equal(Convert.FromHexString("000000000000000000000000"), payload);
        using var writer = new PacketWriter();
        new ReferenceData(DynamicAppearanceReference.TypeName, payload).WriteTo(writer);
        Assert.Equal(52, writer.Position);
        Assert.Equal(
            "171C20" +
            "44796E616D6963417070656172616E6365446566696E6974696F6E73" +
            "00" + "0C000000" + "0C000000" +
            "000000000000000000000000",
            Convert.ToHexString(writer.Written));
    }

    [Fact]
    public void StarterTableContainsSkinAndCranberryOutfitGroupsAndConsumesExactly()
    {
        ReferenceData packet = DynamicAppearanceReference.CreateStarterAppearance();
        Assert.Equal(DynamicAppearanceReference.TypeName, packet.TypeName);
        Assert.Equal(2_252, packet.Payload.Length);

        var reader = new PacketReader(packet.Payload);
        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal(42, reader.ReadInt32());
        var groups = new HashSet<uint>();
        for (int index = 0; index < 42; index++)
        {
            groups.Add(reader.ReadUInt32());
            Assert.StartsWith("BaseTint", reader.ReadString());
            for (int component = 0; component < 4; component++)
            {
                Assert.True(float.IsFinite(reader.ReadSingle()));
            }

            Assert.Equal(0u, reader.ReadUInt32());
            Assert.False(reader.ReadBool());
            Assert.Equal(string.Empty, reader.ReadString());
            Assert.Equal(DynamicAppearanceReference.Float4Type, reader.ReadUInt32());
        }

        Assert.True(reader.AtEnd);
        Assert.Equal(
            new uint[]
            {
                662,
                664,
                665,
                666,
                DynamicAppearanceReference.CranberryShirtGroup,
                DynamicAppearanceReference.CranberryPantsGroup,
                DynamicAppearanceReference.CranberryGearGroup,
            },
            groups.Order());
    }
}
