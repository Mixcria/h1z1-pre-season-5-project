using Cranberry.Tools.ConsoleOpener;

namespace ConsoleOpener.Tests;

/// <summary>
/// The build gate: the one lock that stops this tool writing into a binary its RVAs did not come
/// from. Nothing here opens a process — only files.
/// </summary>
public sealed class BuildGateTests
{
    private const string ClientPath = @"C:\Aug2017\Client\H1Z1.exe";

    /// <summary>Exactly one row, and it is the August client. A second row means someone added a
    /// build; that is a decision, not an accident, so it should break this test on purpose.</summary>
    [Fact]
    public void TheOnlyKnownBuildIsTheAugustClient()
    {
        KnownBuild build = Assert.Single(BuildGate.KnownBuilds);

        Assert.Equal(72_818_304L, build.Size);
        Assert.Equal("d949d39f45074f2b223257477803a8858b4970242c6963df9a213a169d8929dd", build.Sha256);
        Assert.Contains("208059", build.Description);
    }

    [Fact]
    public void TheInstalledClientPassesTheGate()
    {
        if (!File.Exists(ClientPath))
        {
            return;   // the client is not installed on this machine
        }

        var gate = new BuildGate(anyBuild: false);

        Assert.True(gate.IsKnownFile(ClientPath, out string why), why);
        Assert.Contains("build verified", why);
    }

    [Fact]
    public void AFileThatIsNotTheClientIsRefused()
    {
        string path = Path.Combine(Path.GetTempPath(), $"consoleopener-gate-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[64]);

        try
        {
            var gate = new BuildGate(anyBuild: false);

            Assert.False(gate.IsKnownFile(path, out string why));
            Assert.Contains("NOT the build these RVAs came from", why);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// <c>--any-build</c> (with its second flag) turns a refusal into an allow — and says so loudly
    /// in the same string that is written to the status log.
    /// </summary>
    [Fact]
    public void AnyBuildTurnsARefusalIntoALoudAllow()
    {
        string path = Path.Combine(Path.GetTempPath(), $"consoleopener-gate-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[64]);

        try
        {
            var gate = new BuildGate(anyBuild: true);

            Assert.True(gate.IsKnownFile(path, out string why));
            Assert.Contains("patching ANYWAY", why);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingFileIsRefused()
    {
        var gate = new BuildGate(anyBuild: false);
        string path = Path.Combine(Path.GetTempPath(), $"consoleopener-missing-{Guid.NewGuid():N}.bin");

        Assert.False(gate.IsKnownFile(path, out string why));
        Assert.Contains("not readable", why);
    }
}
