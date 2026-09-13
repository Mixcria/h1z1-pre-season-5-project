using System.Net;
using Cranberry.Host;
using Cranberry.Transport;

namespace Cranberry.Tests.Host;

public sealed class HostDiagnosticsTests
{
    private static string Fixture(string filename) => Path.Combine(Path.GetTempPath(), "cranberry-diagnostics", Guid.NewGuid().ToString("N"), filename);

    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    public void DefaultAndEnabledPacketRecordingStillWriteSessionMetadata(string? setting)
    {
        string path = Fixture("wire.txt");
        var request = new SessionRequest(3, 1, 512, "ExternalGatewayApi_3");
        using (var recorder = FilePacketRecorder.FromEnvironment(path, _ => setting))
            recorder.RecordSession(new IPEndPoint(IPAddress.Loopback, 20043), in request);

        Assert.Contains("session-request", File.ReadAllText(path));
    }

    [Fact]
    public void DisabledPacketRecordingCreatesNoFilesAndDoesNotInspectOrAllocatePayloads()
    {
        string path = Fixture("wire.txt");
        using var recorder = FilePacketRecorder.FromEnvironment(path, _ => "0");
        using var second = FilePacketRecorder.FromEnvironment(Fixture("second.txt"), _ => "0");
        Assert.Same(recorder, second);
        // A disabled recorder must return before touching the connection or formatting a payload.
        recorder.RecordMessage(null!, "c2s", [1, 2, 3]);
        recorder.RecordRaw(null!, 42, [4, 5, 6]);
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.Equal("disabled", recorder.FilePath);
    }

    [Fact]
    public void DefaultLoggingKeepsTheDebugFileAndInvalidLevelsKeepThatDefault()
    {
        foreach (string? level in new string?[] { null, "invalid", "999" })
        {
            string path = Fixture("host.log");
            using (var log = ConsoleFileLog.FromEnvironment(path, key => key == "CRANBERRY_LOG_LEVEL" ? level : null))
            {
                Assert.True(log.IsEnabled(TransportLogLevel.Debug));
                log.Debug("debug-default-fixture");
            }
            Assert.Contains("debug-default-fixture", File.ReadAllText(path));
        }
    }

    [Fact]
    public void CloudLoggingCreatesNoFileAndFiltersBelowInfo()
    {
        string path = Fixture("host.log");
        using var log = ConsoleFileLog.FromEnvironment(path, key => key switch
        {
            "CRANBERRY_FILE_LOG" => "0",
            "CRANBERRY_LOG_LEVEL" => "INFO",
            _ => null
        });
        Assert.False(log.IsEnabled(TransportLogLevel.Debug));
        Assert.True(log.IsEnabled(TransportLogLevel.Info));
        log.Debug("suppressed-debug-fixture");
        log.Info("cloud-info-fixture");
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void AsynchronousLoggingKeepsEveryLineInOrderAfterDispose()
    {
        string path = Fixture("host.log");
        const int lines = 5000;
        using (var log = new ConsoleFileLog(path, TransportLogLevel.Debug, writeFile: true, console: TextWriter.Null))
        {
            for (int i = 0; i < lines; i++) log.Debug($"ordered-line-{i:D5}");
            Assert.Equal(0, log.DroppedLines);
        }

        string[] written = File.ReadAllLines(path);
        Assert.Equal(lines, written.Length);
        for (int i = 0; i < lines; i++) Assert.EndsWith($"DBG ordered-line-{i:D5}", written[i]);
    }

    [Fact]
    public void FullQueueDropsLinesCountsThemAndAnnouncesTheBurst()
    {
        string path = Fixture("host.log");
        using var console = new GatedWriter();
        var log = new ConsoleFileLog(path, TransportLogLevel.Debug, writeFile: true, queueCapacity: 4, console: console);
        log.Info("first");
        Assert.True(console.Entered.Wait(TimeSpan.FromSeconds(5)), "the writer never started its first batch");
        // The writer is blocked inside the console write holding "first"; four fit the queue, six do not.
        for (int i = 0; i < 10; i++) log.Info($"queued-{i}");
        Assert.Equal(6, log.DroppedLines);
        console.Release.Set();
        log.Dispose();

        string text = File.ReadAllText(path);
        Assert.Contains("INF first", text);
        for (int i = 0; i < 4; i++) Assert.Contains($"INF queued-{i}", text);
        for (int i = 4; i < 10; i++) Assert.DoesNotContain($"queued-{i}", text);
        Assert.Contains("6 line(s) dropped", text);
    }

    [Fact]
    public void DisposeLeavesABacklogToTheWriterInsteadOfCuttingItOff()
    {
        string path = Fixture("host.log");
        using var console = new GatedWriter();
        var log = new ConsoleFileLog(path, TransportLogLevel.Debug, writeFile: true, queueCapacity: 64, console: console);
        log.Info("first");
        Assert.True(console.Entered.Wait(TimeSpan.FromSeconds(5)), "the writer never started its first batch");
        for (int i = 0; i < 8; i++) log.Info($"late-{i}");
        // The writer is still parked inside the console write; Dispose's wait expires first.
        log.Dispose();
        console.Release.Set();
        Assert.True(SpinWait.SpinUntil(() => ReadShared(path).Contains("late-7"), TimeSpan.FromSeconds(5)),
            "the backlog queued before Dispose never reached the file");

        string text = ReadShared(path);
        Assert.Contains("INF first", text);
        for (int i = 0; i < 8; i++) Assert.Contains($"INF late-{i}", text);
        Assert.Null(log.WriteError);
    }

    [Fact]
    public void AFailingConsoleIsRetiredAloneAndAnnouncedInTheFile()
    {
        string path = Fixture("host.log");
        using var log = new ConsoleFileLog(path, TransportLogLevel.Debug, writeFile: true, console: new ThrowingWriter());
        log.Info("before-console-failure");
        Assert.True(SpinWait.SpinUntil(() => ReadShared(path).Contains("before-console-failure"), TimeSpan.FromSeconds(5)));
        log.Info("after-console-failure");
        log.Dispose();

        string text = ReadShared(path);
        Assert.Contains("INF before-console-failure", text);
        Assert.Contains("console write failed", text);
        Assert.Contains("INF after-console-failure", text);
        Assert.Null(log.WriteError);
        Assert.Null(log.FileError);
    }

    [Fact]
    public void AWriterWithNoSinkLeftCountsEveryLaterLineAsDropped()
    {
        string path = Fixture("host.log");
        using var log = new ConsoleFileLog(path, TransportLogLevel.Debug, writeFile: false, console: new ThrowingWriter());
        log.Info("doomed");
        Assert.True(SpinWait.SpinUntil(() => log.WriteError is not null, TimeSpan.FromSeconds(5)), "the writer never reported its failure");
        // The cause survives, and the batch that was in hand when the sinks died is counted.
        Assert.IsType<IOException>(log.WriteError!.InnerException);
        Assert.Contains("journald went away", log.WriteError.Message);
        Assert.True(log.DroppedLines >= 1, "the in-flight batch was not counted as dropped");
        long before = log.DroppedLines;
        log.Info("one");
        log.Info("two");
        Assert.Equal(before + 2, log.DroppedLines);
        Assert.False(log.Flush(TimeSpan.FromMilliseconds(50)));
    }

    /// <summary>Reads a log the writer thread may still hold open for writing.</summary>
    private static string ReadShared(string path)
    {
        if (!File.Exists(path)) return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class ThrowingWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(string? value) => throw new IOException("journald went away");
    }

    private sealed class GatedWriter : TextWriter
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(string? value)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Entered.Dispose(); Release.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
