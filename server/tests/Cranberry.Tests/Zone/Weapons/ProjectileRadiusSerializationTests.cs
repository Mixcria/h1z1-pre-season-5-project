using System.Buffers.Binary;
using System.Text;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone.Weapons;

public sealed class ProjectileRadiusSerializationTests
{
    [Fact]
    public void NamedProjectilePreservesRadiusListTailAndFollowingRecord()
    {
        // The August reader FUN_140a4caf0 consumes a counted u32 list here, and
        // FUN_142224ef0 consumes its first two entries when preparing a spawn.
        var projectile = new ProjectileDefinitionRecord(71065, "Projectile_Grenades_HEGrenade.adr")
        {
            BulletRadii = [1u, 0u],
            AngularVelocityMin = -6f,
            AngularVelocityMax = 6f,
        };
        var next = new ProjectileDefinitionRecord(73236);
        byte[] table = new ProjectileDefinitionsBlob([projectile, next]).Table();

        Assert.Equal(4 + projectile.Length + next.Length, table.Length);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(table));

        // The fixed wire layout reaches the radius count 144 bytes into the record,
        // plus its three strings' UTF-8 bytes; nine scalar words follow the list.
        int countOffset = 4 + 144 + Encoding.UTF8.GetByteCount(projectile.ModelFileName);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(countOffset)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(countOffset + 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(countOffset + 8)));
        int tailOffset = countOffset + 12;
        for (int axis = 0; axis < 3; axis++)
        {
            Assert.Equal(-6f, BinaryPrimitives.ReadSingleLittleEndian(table.AsSpan(tailOffset + 8 + axis * 8)));
            Assert.Equal(6f, BinaryPrimitives.ReadSingleLittleEndian(table.AsSpan(tailOffset + 12 + axis * 8)));
        }

        Assert.Equal(next.ProjectileId, BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(tailOffset + 36)));
    }
}
