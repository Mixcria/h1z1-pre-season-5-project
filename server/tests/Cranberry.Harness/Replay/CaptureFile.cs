using System.Globalization;

namespace Cranberry.Harness.Replay;

/// <summary>Which of the four <c>direction</c> values a capture line carries.</summary>
public enum CaptureDirection
{
    /// <summary>The SOE SessionRequest line; it has no bytes, only <c>crc=/id=/udp=</c>.</summary>
    SessionRequest,

    /// <summary>An application message the client sent, after decryption and reassembly.</summary>
    ClientToServer,

    /// <summary>An application message the server sent, before framing and encryption.</summary>
    ServerToClient,

    /// <summary>The raw ciphertext of a client datagram, tagged with its keystream position.</summary>
    ClientToServerRaw,
}

/// <summary>
/// One line of a <c>captures\wire-*.txt</c> file.
///
/// The format is <c>time | remote | protocol | direction | length | bytes</c> and it is written by
/// <c>Cranberry.Host.FilePacketRecorder</c> from inside the transport, which makes it a much better
/// replay source than a host log: the <c>c2s</c> and <c>s2c</c> lines are complete <b>application
/// messages</b> — already decrypted, already reassembled, with no SOE framing, no acks and no
/// fragments. So a replay never has to reproduce a recorded session id, sequence number or RC4
/// keystream; it only has to say the same application messages in the same order.
/// </summary>
public sealed record CaptureLine(
    TimeSpan At,
    string Remote,
    string Protocol,
    CaptureDirection Direction,
    long KeystreamPosition,
    int Length,
    string Hex)
{
    /// <summary>The <c>crc=…</c> / <c>id=…</c> / <c>udp=…</c> tail of a session-request line.</summary>
    public string SessionText => Hex;
}

/// <summary>
/// Streams a capture off disk. Captures reach 68 MB and single <c>s2c</c> lines reach 2.5 MB of
/// hex (the 1.2 MB <c>ReferenceData</c> blob), so every method here is a lazy enumerator and the
/// byte arrays are materialised by <see cref="CaptureSessionReader"/> under a size cap.
/// </summary>
public static class CaptureFile
{
    public const string HeaderLine = "# time | remote | protocol | direction | length | bytes";

    /// <summary>Every parsable line, in file order. Malformed lines are skipped, not guessed at.</summary>
    public static IEnumerable<CaptureLine> Read(string path)
    {
        TimeSpan previous = TimeSpan.Zero;
        TimeSpan dayOffset = TimeSpan.Zero;

        foreach (string raw in ReadLinesSharing(path))
        {
            if (raw.Length == 0 || raw[0] == '#')
            {
                continue;
            }

            string[] parts = raw.Split(" | ", 6);
            if (parts.Length < 6)
            {
                continue;
            }

            if (!TimeSpan.TryParseExact(parts[0], @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out TimeSpan at))
            {
                continue;
            }

            // A capture that runs past midnight would otherwise go backwards in time and turn
            // every later delay negative. Wall-clock is all the recorder writes, so this is the
            // only repair available, and it is exact for any run shorter than a day.
            if (at + dayOffset < previous - TimeSpan.FromMinutes(5))
            {
                dayOffset += TimeSpan.FromDays(1);
            }

            at += dayOffset;
            previous = at;

            (CaptureDirection direction, long keystream) = ParseDirection(parts[3]);
            if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int length))
            {
                continue;
            }

            yield return new CaptureLine(at, parts[1], parts[2], direction, keystream, length, parts[5]);
        }
    }

    /// <summary>
    /// Reads a capture that a host may still be appending to.
    /// <c>File.ReadLines</c> asks for <c>FileShare.Read</c>, which is refused while
    /// <c>FilePacketRecorder</c> holds the file open for writing — and the capture a running host
    /// is writing right now is precisely the one worth reading. A partly flushed final line is
    /// discarded by the field count or the length check downstream.
    /// </summary>
    private static IEnumerable<string> ReadLinesSharing(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is string line)
        {
            yield return line;
        }
    }

    private static (CaptureDirection Direction, long Keystream) ParseDirection(string field)
    {
        if (field == "c2s")
        {
            return (CaptureDirection.ClientToServer, -1);
        }

        if (field == "s2c")
        {
            return (CaptureDirection.ServerToClient, -1);
        }

        if (field == "session-request")
        {
            return (CaptureDirection.SessionRequest, -1);
        }

        if (field.StartsWith("c2s-raw@", StringComparison.Ordinal)
            && long.TryParse(field.AsSpan(8), NumberStyles.Integer, CultureInfo.InvariantCulture, out long position))
        {
            return (CaptureDirection.ClientToServerRaw, position);
        }

        return (CaptureDirection.ClientToServerRaw, -1);
    }
}
