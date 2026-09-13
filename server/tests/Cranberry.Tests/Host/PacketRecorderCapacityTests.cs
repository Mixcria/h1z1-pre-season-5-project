using System.Net;
using Cranberry.Host;
using Cranberry.Transport;

namespace Cranberry.Tests.Host;

public sealed class PacketRecorderCapacityTests
{
    [Fact]
    public void DisposeDrainsAcceptedRecordsAndOverloadIsExplicit()
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-capture-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "wire.txt");
        var recorder = new FilePacketRecorder(path, queueCapacity: 8, maxQueuedChars: 2048);
        var request = new SessionRequest(3, 1, 512, "Test");
        for (int n = 0; n < 10000; n++) recorder.RecordSession(new(IPAddress.Loopback, n + 1), in request);
        recorder.Dispose();
        string[] lines = File.ReadAllLines(path);
        Assert.Equal(10000, lines.Count(line => !line.StartsWith('#')) + recorder.DroppedRecords);
        Assert.Null(recorder.WriteError);
        if (recorder.DroppedRecords > 0)
            Assert.Contains(lines, line => line.Contains($"{recorder.DroppedRecords} records dropped in total"));
        recorder.Dispose();
    }
}
