using Cranberry.Login;
using System.Text.Json;

namespace Cranberry.Tests.Login;

public sealed class RosterPersistenceTests
{
    private static CharacterCreatePayload Payload(string name, uint gender) => new(
        EmpireId: 2, HeadId: 3, ProfileId: 270, Gender: gender, Name: name, SkinToneId: 664, HairId: 2,
        RuntimeValue: 2, OperatingSystem: "Windows 10 Home", PlatformVersion: "6.2", ClientVersion: "0.0.118.208059", Environment: "Live");

    [Fact]
    public void CharactersAndTheNextIdSurviveAReload()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cranberry-roster-{Guid.NewGuid():N}", "roster.json");
        try
        {
            CharacterRosterStore first = CharacterRosterStore.Load(path);
            Assert.Empty(first.Snapshot());
            CharacterEntry adada = first.Create(1, Payload("adada", 2));
            CharacterEntry sam = first.Create(1, Payload("sam", 2));
            Assert.True(first.Remove(adada.EntityKey));
            Assert.True(File.Exists(path));

            CharacterRosterStore second = CharacterRosterStore.Load(path);
            CharacterEntry[] rows = second.Snapshot();
            Assert.Single(rows);
            Assert.Equal(sam.EntityKey, rows[0].EntityKey);
            Assert.Equal("sam", rows[0].Name);
            Assert.Equal(sam.Payload, rows[0].Payload);
            Assert.True(second.ContainsAvailable(sam.EntityKey, 1));

            // Ids keep counting from where the file left off — a deleted key is never reused.
            CharacterEntry third = second.Create(1, Payload("third", 1));
            Assert.Equal(sam.EntityKey + 0x10, third.EntityKey);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void InMemoryStoreStillWorksWithoutAPath()
    {
        var store = new CharacterRosterStore();
        CharacterEntry entry = store.Create(1, Payload("x", 1));
        Assert.Null(store.Path);
        Assert.True(store.TryGet(entry.EntityKey, 1, out _));
    }

    [Fact]
    public void EveryCreatedCharacterHasTheNativePlayerType()
    {
        var store = new CharacterRosterStore();
        for (int index = 0; index < 32; index++)
        {
            Assert.True(store.TryCreateUnique(1, Payload($"player{index}", 1), out CharacterEntry entry));
            Assert.Equal(1UL, entry.EntityKey & 0xFUL); // Native CreateNewOrder player-GUID guard.
            Assert.Equal(0x1001UL + (ulong)index * 0x10, entry.EntityKey);
        }
        CharacterEntry[] rows = store.Snapshot();
        Assert.Equal(32, rows.Select(row => row.EntityKey).Distinct().Count());
    }

    [Fact]
    public void RemovingTheHighestCharacterDoesNotReuseItsIdAfterReload()
    {
        WithRosterFile(path =>
        {
            CharacterRosterStore first = CharacterRosterStore.Load(path);
            CharacterEntry low = first.Create(1, Payload("low", 1));
            CharacterEntry high = first.Create(1, Payload("high", 1));
            Assert.True(first.Remove(high.EntityKey));
            Assert.True(first.Remove(low.EntityKey));

            CharacterRosterStore second = CharacterRosterStore.Load(path);
            Assert.Empty(second.Snapshot());
            CharacterEntry next = second.Create(1, Payload("next", 1));
            Assert.Equal(high.EntityKey + 0x10, next.EntityKey);
            Assert.Equal(1UL, next.EntityKey & 0xFUL);
        });
    }

    [Theory]
    [InlineData(0x1005L, 0x1005UL, 0x1011UL)]
    [InlineData(0x1000L, 0x1051UL, 0x1061UL)]
    [InlineData(0x1100L, 0x1005UL, 0x1101UL)]
    public void LegacyRowsRemainIntactAndAllocationClearsBothHighWaterMarks(
        long savedHighWater, ulong legacyId, ulong expectedNext)
    {
        WithRosterFile(path =>
        {
            WriteRoster(path, savedHighWater, legacyId);
            byte[] beforeLoad = File.ReadAllBytes(path);
            CharacterRosterStore store = CharacterRosterStore.Load(path);
            CharacterEntry legacy = Assert.Single(store.Snapshot());
            Assert.Equal(legacyId, legacy.EntityKey);
            Assert.Equal(beforeLoad, File.ReadAllBytes(path)); // Loading performs no implicit migration.

            CharacterEntry created = store.Create(1, Payload("new", 1));
            Assert.Equal(expectedNext, created.EntityKey);
            CharacterRosterStore reloaded = CharacterRosterStore.Load(path);
            Assert.True(reloaded.TryGet(legacyId, 1, out CharacterEntry preserved));
            Assert.Equal(legacy.Name, preserved.Name);
            Assert.Equal(legacy.Gender, preserved.Gender);
            Assert.Equal(legacy.Payload, preserved.Payload);
            Assert.Equal(expectedNext + 0x10, reloaded.Create(1, Payload("after reload", 1)).EntityKey);
        });
    }

    [Fact]
    public void LastRepresentableCharacterCanBeCreatedOnlyOnce()
    {
        WithRosterFile(path =>
        {
            WriteRoster(path, long.MaxValue - 30);
            CharacterRosterStore store = CharacterRosterStore.Load(path);
            CharacterEntry last = store.Create(1, Payload("last", 1));
            Assert.Equal((ulong)(long.MaxValue - 14), last.EntityKey);
            Assert.Equal(1UL, last.EntityKey & 0xFUL);
            byte[] saved = File.ReadAllBytes(path);
            Assert.Throws<OverflowException>(() => store.Create(1, Payload("overflow", 1)));
            Assert.Equal(last, Assert.Single(store.Snapshot()));
            Assert.Equal(saved, File.ReadAllBytes(path));

            CharacterRosterStore reloaded = CharacterRosterStore.Load(path);
            Assert.Throws<OverflowException>(() => reloaded.TryCreateUnique(1, Payload("retry", 1), out _));
            Assert.Equal(last.EntityKey, Assert.Single(reloaded.Snapshot()).EntityKey);
            Assert.Equal(saved, File.ReadAllBytes(path));
        });
    }

    [Theory]
    [InlineData(long.MaxValue - 14)]
    [InlineData(long.MaxValue)]
    [InlineData(-1L)]
    public void ExhaustedHighWaterNeverMutatesOrWraps(long highWater)
    {
        WithRosterFile(path =>
        {
            WriteRoster(path, highWater);
            byte[] saved = File.ReadAllBytes(path);
            CharacterRosterStore store = CharacterRosterStore.Load(path);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Assert.Throws<OverflowException>(() => store.Create(1, Payload("overflow", 1)));
                Assert.Empty(store.Snapshot());
                Assert.Equal(saved, File.ReadAllBytes(path));
            }
        });
    }

    private static void WriteRoster(string path, long highWater, params ulong[] ids)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            NextEntityKey = highWater,
            Characters = ids.Select(id => new
            {
                EntityKey = id, ServerId = 1UL, Field3 = 0UL, Status = CharacterEntry.StatusAvailable,
                Payload = Convert.ToBase64String(CharacterSelectionPayload.FromCreate(Payload("legacy", 2)).ToArray()),
                Name = "legacy", Gender = 2U,
            }).ToArray(),
        }));
    }

    private static void WithRosterFile(Action<string> test)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cranberry-roster-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try { test(Path.Combine(directory, "roster.json")); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
