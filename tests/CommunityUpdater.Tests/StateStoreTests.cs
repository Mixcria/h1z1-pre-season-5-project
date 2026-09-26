using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cranberry.Launcher.Core;

namespace CommunityUpdater.Tests;

public sealed class StateStoreTests
{
    [WindowsTheory]
    [InlineData(50)]
    [InlineData(250)]
    public void WriterWaitsForPreviewOneReaderAndPublishesCompleteState(int holdMilliseconds)
    {
        using var fixture = new UpdateFixture();
        var store = new CommunityUpdateStateStore(fixture.Root, fixture.Trust);
        var oldState = new CommunityUpdateState(1, 1, null, null, null, []);
        var newState = oldState with { HighestAccepted = 2, Failed = [new string('A', 64)] };
        store.Write(oldState);
        string path = Path.Combine(store.Root, "state.json");
        // These are the shipped preview-1 parent's exact ReadReceipt flags.
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var ready = new ManualResetEventSlim();
        using var beginWrite = new ManualResetEventSlim();
        string? initialRead = null, heldRead = null;
        Exception? readerFailure = null;
        var releaseReader = new Thread(() =>
        {
            try
            {
                initialRead = ReadComplete(reader);
                ready.Set();
                if (!beginWrite.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Writer did not start.");
                Thread.Sleep(holdMilliseconds);
                reader.Position = 0;
                heldRead = ReadComplete(reader);
            }
            catch (Exception error) { readerFailure = error; }
            finally { reader.Dispose(); ready.Set(); }
        }) { IsBackground = true };
        // A prestarted dedicated thread releases the reader even while the test writer blocks.
        releaseReader.Start();
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)), "Reader did not start.");
            Assert.Null(readerFailure);
            beginWrite.Set();
            store.Write(newState);
        }
        finally
        {
            beginWrite.Set();
            Assert.True(releaseReader.Join(TimeSpan.FromSeconds(5)), "Reader did not release its handle.");
        }

        Assert.Null(readerFailure);
        Assert.Equal(JsonSerializer.Serialize(oldState), initialRead);
        Assert.Equal(initialRead, heldRead);
        Assert.Equal(JsonSerializer.Serialize(newState), File.ReadAllText(path));
        Assert.Equal(2, store.Read().HighestAccepted);
        Assert.Empty(Directory.EnumerateFiles(store.Root, "*.new"));
    }

    [WindowsFact]
    public void PersistentlyHeldReaderFailsWithinTwoSecondsAndPreservesPriorState()
    {
        using var fixture = new UpdateFixture();
        var store = new CommunityUpdateStateStore(fixture.Root, fixture.Trust);
        var oldState = new CommunityUpdateState(1, 1, null, null, null, []);
        store.Write(oldState);
        string path = Path.Combine(store.Root, "state.json");
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        var elapsed = Stopwatch.StartNew();
        Exception? error = Record.Exception(() => store.Write(oldState with { HighestAccepted = 2 }));
        elapsed.Stop();

        Assert.True(error is IOException or UnauthorizedAccessException, $"Expected the original Windows file error, got {error}.");
        Assert.Contains(error!.HResult & 0xffff, new[] { 5, 32, 33 });
        Assert.InRange(elapsed.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        Assert.Equal(JsonSerializer.Serialize(oldState), ReadComplete(reader));
        Assert.Equal(JsonSerializer.Serialize(oldState), File.ReadAllText(path));
        Assert.Equal(1, store.Read().HighestAccepted);
        Assert.Empty(Directory.EnumerateFiles(store.Root, "*.new"));
    }

    private static string ReadComplete(Stream input)
    {
        using var text = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024, leaveOpen: true);
        return text.ReadToEnd();
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        { if (!OperatingSystem.IsWindows()) Skip = "Exercises Windows file replacement and sharing semantics."; }
    }

    private sealed class WindowsTheoryAttribute : TheoryAttribute
    {
        public WindowsTheoryAttribute()
        { if (!OperatingSystem.IsWindows()) Skip = "Exercises Windows file replacement and sharing semantics."; }
    }
}
