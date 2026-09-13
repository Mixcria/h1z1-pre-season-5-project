using System.Globalization;
using Cranberry.Zone;
using Cranberry.Zone.Movement;

namespace Cranberry.Tests.Zone.Movement;

/// <summary>
/// The detector graded against three windows of the owner's own captures, replayed record for
/// record through the same <c>ClientMovementUpdate.Parse</c> the live server uses.
///
/// <para>Two of them are wields that froze him (2026-08-31 18:35 and 19:42) and the third is the
/// same pickup left stowed on the same evening (17:42), where he kept walking. A detector that
/// cannot separate those three is worth nothing, because that separation is the entire claim the
/// wave-11 loop makes on its own without an owner at the keyboard.</para>
///
/// <para>The fixtures under <c>tests/Cranberry.Tests/Fixtures</c> are cuts of the capture files,
/// not synthesised bytes: each line is one client-authored channel-2 payload with the gateway
/// header byte removed, prefixed by its offset in milliseconds from the first record of the cut.
/// Their provenance is in their own header comments.</para>
/// </summary>
public sealed class LocomotionLockDetectorTests
{
    private const string LockDraw1835 = "locomotion-lock-183154-1835.hexdump";
    private const string LockWield1942 = "locomotion-lock-192743-1942.hexdump";
    private const string ClearStowed1742 = "locomotion-clear-173948-1742.hexdump";

    [Fact]
    public void TheOnePacketDrawOf1835LocksTheHand()
    {
        Replayed run = Replay(LockDraw1835);

        Assert.True(run.Locks >= 1, run.Transcript);
        Assert.Contains(run.Lines, line => line.StartsWith("LOCOMOTION LOCK", StringComparison.Ordinal));
    }

    [Fact]
    public void TheEightPacketWieldOf1942LocksTheHand()
    {
        Replayed run = Replay(LockWield1942);

        Assert.True(run.Locks >= 1, run.Transcript);
        Assert.Contains(run.Lines, line => line.StartsWith("LOCOMOTION LOCK", StringComparison.Ordinal));
    }

    /// <summary>
    /// The control. Same evening, same item, same client: <c>WieldFirstWeapon</c> off, so the gun
    /// went to body slot 76 and nothing was ever bound to slot 7. The player moved the whole time,
    /// and the detector must never once say LOCK — a detector that fires here would fail every
    /// future fix run for free.
    /// </summary>
    [Fact]
    public void TheStowedPickupOf1742NeverLocks()
    {
        Replayed run = Replay(ClearStowed1742);

        Assert.Equal(0, run.Locks);
        Assert.Equal(LocomotionLockState.Clear, run.Final);
    }

    /// <summary>
    /// The turn lock is the second, independent observable (refute-2 §3.2): the client repeats one
    /// yaw for the whole freeze while it keeps sending rotation records.
    ///
    /// <para>Only 18:35 is asserted. Its freeze carries twelve yaw readings, all
    /// <c>-1.5142624378204346</c>. The 19:42 freeze carries <b>one</b> rotation-only record in 5.55 s
    /// (S5a §3.3's per-window table), which is below any honest threshold for "the angle is not
    /// changing" — so the sub-signal correctly declines to speak there, and the LOCK verdict itself
    /// is what grades that window.</para>
    /// </summary>
    [Fact]
    public void TheFrozenWindowOf1835AlsoReportsAPinnedYaw() =>
        Assert.True(Replay(LockDraw1835).YawPinnedWhileLocked, "18:35 reported a moving yaw for the whole lock");

    /// <summary>The stowed control turns on the spot, which is what the sub-signal must not call pinned.</summary>
    [Fact]
    public void TheStowedControlTurnsOnTheSpot()
    {
        Replayed run = Replay(ClearStowed1742);

        Assert.True(run.MaximumDistinctYaw > 1, $"only {run.MaximumDistinctYaw} distinct yaw value(s) in the control");
    }

    [Fact]
    public void ALineIsEmittedOnlyWhenTheVerdictChanges()
    {
        Replayed run = Replay(LockWield1942);

        Assert.True(run.Lines.Count < run.Records / 4, $"{run.Lines.Count} line(s) for {run.Records} records");
        Assert.All(run.Lines, line => Assert.Contains("distinctYaw=", line, StringComparison.Ordinal));
        Assert.All(run.Lines, line => Assert.Contains("displacement=", line, StringComparison.Ordinal));
        Assert.All(run.Lines, line => Assert.Contains("input=", line, StringComparison.Ordinal));
        Assert.All(run.Lines, line => Assert.Contains("records=", line, StringComparison.Ordinal));
    }

    /// <summary>A detector that has never seen the player move refuses to call a freeze (see the class doc).</summary>
    [Fact]
    public void AFreshDetectorNeverLocksBeforeItHasSeenMovement()
    {
        var detector = new LocomotionLockDetector();
        foreach ((long at, ClientMovementUpdate update) in Load(LockDraw1835))
        {
            // Only the frozen half of the cut: the run-up that arms the detector is skipped.
            if (at < 2_500)
            {
                continue;
            }

            detector.Observe(update, at);
        }

        Assert.Equal(0, detector.LockCount);
    }

    [Fact]
    public void ResetForgetsTheWindow()
    {
        var detector = new LocomotionLockDetector();
        foreach ((long at, ClientMovementUpdate update) in Load(LockWield1942))
        {
            detector.Observe(update, at);
        }

        detector.Reset();

        Assert.Equal(LocomotionLockState.Unknown, detector.State);
        Assert.Equal(0, detector.Measure(0).Records);
    }

    private static Replayed Replay(string fixture)
    {
        var detector = new LocomotionLockDetector();
        List<string> lines = [];
        var transcript = new System.Text.StringBuilder(fixture).AppendLine();
        int locks = 0;
        int records = 0;
        int maximumDistinctYaw = 0;
        bool yawPinnedWhileLocked = false;

        foreach ((long at, ClientMovementUpdate update) in Load(fixture))
        {
            records++;
            string? line = detector.Observe(update, at);
            maximumDistinctYaw = Math.Max(maximumDistinctYaw, detector.Last.DistinctYawValues);
            if (detector.State == LocomotionLockState.Locked)
            {
                yawPinnedWhileLocked |= detector.Last.YawPinned;
            }

            if (line is null)
            {
                continue;
            }

            lines.Add(line);
            transcript.Append(CultureInfo.InvariantCulture, $"  +{at} ms  {line}").AppendLine();
            if (detector.Last.State == LocomotionLockState.Locked)
            {
                locks++;
            }
        }

        return new Replayed(lines, locks, records, detector.State, maximumDistinctYaw, yawPinnedWhileLocked, transcript.ToString());
    }

    private static IEnumerable<(long At, ClientMovementUpdate Update)> Load(string fixture)
    {
        foreach (string raw in File.ReadLines(Path.Combine(FixtureDirectory(), fixture)))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int space = line.IndexOf(' ', StringComparison.Ordinal);
            long at = long.Parse(line[..space], CultureInfo.InvariantCulture);
            yield return (at, ClientMovementUpdate.Parse(Convert.FromHexString(line[(space + 1)..])));
        }
    }

    /// <summary>
    /// Finds the source fixtures from both the normal test-project output and isolated artifacts
    /// beneath the repository root, without copying captures into every build output.
    /// </summary>
    private static string FixtureDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "Fixtures");
            if (File.Exists(Path.Combine(candidate, LockDraw1835)))
            {
                return candidate;
            }

            candidate = Path.Combine(directory.FullName, "tests", "Cranberry.Tests", "Fixtures");
            if (File.Exists(Path.Combine(candidate, LockDraw1835)))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"No Fixtures directory holding {LockDraw1835} was found above {AppContext.BaseDirectory}");
    }

    private sealed record Replayed(
        IReadOnlyList<string> Lines,
        int Locks,
        int Records,
        LocomotionLockState Final,
        int MaximumDistinctYaw,
        bool YawPinnedWhileLocked,
        string Transcript);
}
