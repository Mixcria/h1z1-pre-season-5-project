using Cranberry.Launcher.Core;
using System.Security.Cryptography;

namespace Cranberry.Launcher.Tests;

public sealed class BinocularScopeFixTests
{
    private static byte[] Original() => Convert.FromHexString(NativeHex);
    private static byte[] State(int state)
    {
        byte[] bytes = Original();
        if ((state & 1) != 0) bytes[BinocularScopeFix.EntryIndex] = 0x41;
        if ((state & 2) != 0) bytes[BinocularScopeFix.ReleaseIndex] = 0xEB;
        return bytes;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AllPythonRecognizedStatesApplyAndRestoreOnlyTheReviewedBranches(int initial)
    {
        byte[] memory = State(initial);
        Assert.Equal(initial, BinocularScopeFix.Classify(memory));
        var writes = new List<int>();
        void Write(int index, byte from, byte to)
        {
            Assert.Equal(from, memory[index]);
            memory[index] = to;
            writes.Add(index);
        }
        Assert.Equal(initial != 3, BinocularScopeFix.ApplyVerified(() => memory.ToArray(), Write));
        Assert.Equal(State(3), memory);
        Assert.Equal(BinocularScopeFix.PatchedSha256, Convert.ToHexString(SHA256.HashData(memory)));
        if (initial == 0) Assert.Equal([BinocularScopeFix.ReleaseIndex, BinocularScopeFix.EntryIndex], writes);
        Assert.False(BinocularScopeFix.ApplyVerified(() => memory.ToArray(), Write));
        writes.Clear();
        Assert.True(BinocularScopeFix.ApplyVerified(() => memory.ToArray(), Write, restore: true));
        Assert.Equal([BinocularScopeFix.EntryIndex, BinocularScopeFix.ReleaseIndex], writes);
        Assert.Equal(Original(), memory);
        Assert.False(BinocularScopeFix.ApplyVerified(() => memory.ToArray(), Write, restore: true));
        // Exact native jump destinations, including the modified entry displacement.
        Assert.Equal(0x158BB23, BinocularScopeFix.FunctionRva + 0x3E0 + 2 + State(3)[0x3E1]);
        Assert.Equal(0x158BB93, BinocularScopeFix.FunctionRva + 0x45E + 2 + State(3)[0x45F]);
    }

    public static IEnumerable<object[]> Failures()
    {
        for (int initial = 0; initial <= 3; initial++)
        foreach (bool restore in new[] { false, true })
        foreach (bool after in new[] { false, true })
        {
            int target = restore ? 0 : 3;
            int count = ((initial ^ target) & 1) + (((initial ^ target) >> 1) & 1);
            for (int failedWrite = 1; failedWrite <= count; failedWrite++)
                yield return [initial, restore, after, failedWrite];
        }
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public void EveryFailedWriteIncludingCleanupFailureRestoresTheExactStartingState(
        int initial, bool restore, bool afterWrite, int failedWrite)
    {
        byte[] memory = State(initial), before = memory.ToArray();
        int writes = 0;
        Assert.Throws<IOException>(() => BinocularScopeFix.ApplyVerified(() => memory.ToArray(), (index, from, to) =>
        {
            Assert.Equal(from, memory[index]);
            bool fail = ++writes == failedWrite;
            if (fail && !afterWrite) throw new IOException("Write failed before changing memory");
            memory[index] = to;
            if (fail) throw new IOException("Cache cleanup failed after changing memory");
        }, restore));
        Assert.Equal(before, memory);
    }

    [Fact]
    public void ForeignMutationRefusesApplyAndRollbackWithoutOverwritingIt()
    {
        byte[] memory = Original();
        int writes = 0;
        Assert.Throws<AggregateException>(() => BinocularScopeFix.ApplyVerified(() => memory.ToArray(), (index, from, to) =>
        {
            Assert.Equal(1, ++writes);
            memory[index] = to;
            memory[50] ^= 1;
        }));
        Assert.Equal(1, writes);
        Assert.NotEqual(Original()[50], memory[50]);
    }

    [Fact]
    public void EveryUnrecognizedByteAndTruncatedFunctionFailsBeforeAnyWrite()
    {
        byte[] original = Original();
        for (int index = 0; index < original.Length; index++)
        {
            byte[] unknown = original.ToArray();
            unknown[index] ^= 1;
            Assert.Throws<InvalidDataException>(() => BinocularScopeFix.ApplyVerified(() => unknown, (_, _, _) => Assert.Fail("Unexpected write")));
        }
        Assert.Throws<InvalidDataException>(() => BinocularScopeFix.Classify(original[..^1]));
        Assert.Throws<InvalidDataException>(() => BinocularScopeFix.Classify([.. original, 0]));
    }

    [Theory]
    [InlineData(0x140000000L, 0x315A458L)]
    [InlineData(0x150000000L, 0x315AEC0L)]
    public void ReadyWorldRequiresAugustInfantryCameraAndReticleAtTheRelocatedBase(long image, long camera)
    {
        const long client = 0x200000000, manager = 0x200100000, player = 0x200200000,
            controller = 0x200300000, reticle = 0x200400000, datasource = 0x200500000;
        var pointers = new Dictionary<long, long>
        {
            [image + 0x3F696A0] = client, [image + 0x3F69430] = manager, [image + 0x3F6A200] = reticle,
            [manager + 0x1948] = player, [client + 0x321A0] = controller,
            [player] = image + 0x31DDDC0, [controller] = image + camera,
            [reticle + 0x88] = datasource, [datasource] = image + 0x3250158,
        };
        byte[] Read(long address, int size)
        {
            Assert.Equal(8, size);
            return BitConverter.GetBytes(pointers[address]);
        }
        Assert.Equal((player, controller, reticle), BinocularScopeFix.ReadyIdentity(Read, image));
        foreach (long key in pointers.Keys.ToArray())
        {
            long saved = pointers[key];
            pointers[key] = 0;
            Assert.Null(BinocularScopeFix.ReadyIdentity(Read, image));
            pointers[key] = saved;
        }
        pointers[controller] = image + 0x315A459; // Other camera/controller context.
        Assert.Null(BinocularScopeFix.ReadyIdentity(Read, image));
    }

    // Exact 1,551-byte FUN_14158b700 from original August image, file offset 0x158ad00.
    // Whole EXE: D949D39F45074F2B223257477803A8858B4970242C6963DF9A213A169D8929DD.
    // Kept in the test assembly so verification works without an installed game.
    private const string NativeHex =
        "488BC4554154415541564157488DA8F8FCFFFF4881ECE003000048C74550FEFFFFFF488958084889701048897818488B" +
        "D94C8B3D68DF9D024D8BB7902103004D85F60F84A6050000488D35FA139A024889756033FF48897D684C8D258070B601" +
        "4C896558488D1575E6BC01488D4D58E8330EAFFE904C8D2D8C70B6014C896D58488974243848897C24404C8964243048" +
        "8D156AB4B801488D4C2430E8070EAFFE904C896C2430C64424280148897C242041B1014C8D4558488D542430498BCEE8" +
        "FAF9B5FE440FB6A8100100004180E5014C8964243083CEFF397C24447E21488B5424384883C2FC8BC6F00FC10283E801" +
        "7F0D488D4C2430488B442430FF5010488D0D43139A0248894C243848897C2440488D05C96DB60148894424304C896558" +
        "837D6C007E25488B55604883C2FC8BC6F00FC10283E8017F12488D4D58488B4558FF5010488D0DFE129A0248898DC000" +
        "00004889BDC80000004C89A5B8000000488D1579E5BC01488D8DB8000000E8340DAFFE90488D058D6FB601488985B800" +
        "0000488D05C0129A024889459848897DA04C896590488D1564B3B801488D4D90E8020DAFFE90488D055B6FB601488945" +
        "90C64424280148897C242041B1014C8D85B8000000488D5590498BCEE8EDF8B5FE440FB6A0C8000000488D0D006FB601" +
        "48894D90837DA4007E25488B55984883C2FC8BC6F00FC10283E8017F12488D4D90488B4590FF5010488D0DD16EB6014C" +
        "8D0533129A024C89459848897DA0488D05BB6CB6014889459048898DB800000083BDCC000000007E35488B95C0000000" +
        "4883C2FC8BC6F00FC10283E8017F1F488D8DB8000000488B85B8000000FF5010488D0D796EB6014C8D05DB119A024C89" +
        "85200100004889BD2801000048898D18010000488D1556E4BC01488D8D18010000E8110CAFFE90488D056A6EB6014889" +
        "8518010000488D059D119A02488945F848897D00488D05256EB601488945F0488D153AB2B801488D4DF0E8D80BAFFE90" +
        "488D05316EB601488945F0C64424280148897C242041B1014C8D8518010000488D55F0498BCEE8C3F7B5FE448BB01001" +
        "000041D1EE4180E601488D0DD06DB60148894DF0837D04007E25488B55F84883C2FC8BC6F00FC10283E8017F12488D4D" +
        "F0488B45F0FF5010488D0DA16DB601488D0503119A02488945F848897D00488D058B6BB601488945F048898D18010000" +
        "83BD2C010000007E25488B95200100004883C2FCF00FC13283EE017F11488D8D18010000488B8518010000FF5010488B" +
        "83380300000FB688E2370000F6C140751A80F98073154032F6488B03488BCBFF90A001000084C07408EB0340B60141B6" +
        "01488BCBE84FC5B5FE84C074484584E47451488B03488BCBFF90A001000084C07433488B833803000080B82A55000000" +
        "7423498BCFE86E91ADFE83F81E7516488B8338030000C6802A55000000488BCBE86677AFFE4584E474094584F60F85CB" +
        "010000488BCBE8EDC4B5FE84C00F84EC0000004584E47423498BCFE82891ADFE83F8247516488B8338030000C6802A55" +
        "000001488BCBE82077AFFE4584F67433488B833803000080B82A550000007423498BCFE8F090ADFE83F81E7516488B83" +
        "38030000C6802A55000000488BCBE8E876AFFE89BD300200004C8D8D300200004533C0488D15DEA5CE01488B0DD7ABD2" +
        "03E831F8B2FE8B8D3002000083F906750980BD3802000000752A488B833803000080B82A55000000741A4584E4751544" +
        "88A02A550000488BCBE88D76AFFE8B8D3002000083E903741383E904740E83E901740983F9010F85E2000000488D8D38" +
        "020000488B8538020000E9CB00000089BD800100004C8D8D800100004533C0488D1552A5CE01488B0D4BABD203E8A5F7" +
        "B2FE0FB685880100008B8D8001000083F9060F44F84084F6756E4084FF7569488B03488BCBFF90A001000084C0755345" +
        "84ED75054584F67449488BCBE8376CA9FE4885C0743C837824037436488B03488BCBFF90280100000F5405C18CB901F3" +
        "0F100D41527902F30F590DC119B9010F2FC1730E488B03B201488BCBFF90700300008B8D8001000083E903740F83E904" +
        "740A83E901740583F9017512488D8D88010000488B858801000033D2FF104C8D9C24E0030000498B5B30498B7338498B" +
        "7B40498BE3415F415E415D415C5DC3";
}
