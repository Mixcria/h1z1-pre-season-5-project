using System.Globalization;
using Cranberry.Harness.Protocol;

namespace Cranberry.Harness.Replay;

/// <summary>
/// One recorded application message. The bytes are materialised only up to
/// <see cref="CaptureSessionReader.FullByteLimit"/>; beyond that only a prefix is kept and
/// <see cref="Truncated"/> is set.
///
/// That cap is not laziness. A capture of a real session carries a 1.2 MB ReferenceData blob and
/// a 25 KB SendZoneDetails, and the 68 MB host capture of 2026-08-30 holds forty-odd sessions of
/// them; keeping every byte would cost gigabytes to answer a question — "which opcodes, how many,
/// how long" — that needs the first few bytes and the length. Everything the client ever
/// <i>sends</i> is far below the cap, so no replay step is ever truncated; the guard in
/// <see cref="ReplayScript"/> refuses to send one if that ever stops being true.
/// </summary>
public sealed class CaptureMessage
{
    internal CaptureMessage(TimeSpan at, CaptureDirection direction, int length, byte[] bytes, bool truncated, PacketSignature signature)
    {
        At = at;
        Direction = direction;
        Length = length;
        Bytes = bytes;
        Truncated = truncated;
        Signature = signature;
    }

    public TimeSpan At { get; }

    public CaptureDirection Direction { get; }

    /// <summary>The length the recorder wrote, whatever <see cref="Bytes"/> holds.</summary>
    public int Length { get; }

    /// <summary>The message bytes, or its first <see cref="CaptureSessionReader.FullByteLimit"/>.</summary>
    public byte[] Bytes { get; }

    public bool Truncated { get; }

    public PacketSignature Signature { get; }

    public override string ToString() => $"{At.TotalSeconds,8:F3}s {Direction} {Signature.Name} ({Length} B)";
}

/// <summary>One SOE session in a capture: everything recorded for one remote UDP endpoint.</summary>
public sealed class CaptureSession
{
    private readonly List<CaptureMessage> _messages = [];

    internal CaptureSession(string remote, string protocol)
    {
        Remote = remote;
        Protocol = protocol;
        Link = protocol == AugustClient.LoginProtocolName ? CaptureLink.Login : CaptureLink.Gateway;
    }

    public string Remote { get; }

    public string Protocol { get; }

    public CaptureLink Link { get; }

    /// <summary>The session id the client asked for, from the <c>session-request</c> line.</summary>
    public uint SessionId { get; internal set; }

    public uint CrcLength { get; internal set; }

    public uint UdpLength { get; internal set; }

    public TimeSpan StartedAt { get; internal set; }

    public TimeSpan EndedAt { get; internal set; }

    public TimeSpan Duration => EndedAt - StartedAt;

    /// <summary>Application packets explicitly omitted by the host's privacy recorder.</summary>
    public int RedactedMessages { get; internal set; }

    /// <summary>Application messages both ways, in recorded order, offset from the session start.</summary>
    public IReadOnlyList<CaptureMessage> Messages => _messages;

    public IEnumerable<CaptureMessage> ClientToServer =>
        _messages.Where(m => m.Direction == CaptureDirection.ClientToServer);

    public IEnumerable<CaptureMessage> ServerToClient =>
        _messages.Where(m => m.Direction == CaptureDirection.ServerToClient);

    internal void Add(CaptureMessage message) => _messages.Add(message);

    public override string ToString() =>
        $"{Protocol} {Remote} session {SessionId:x8} — {_messages.Count} message(s) over {Duration.TotalSeconds:F1}s";
}

/// <summary>
/// A login session paired with the gateway session that followed it: one attempt by one client to
/// get into the world. A capture usually holds several — the two known-bad reference captures hold
/// four and three respectively, because the owner retried — so a replay must be told which.
/// </summary>
public sealed record CaptureRun(string Path, int Index, CaptureSession? Login, CaptureSession Gateway)
{
    public TimeSpan StartedAt => Login?.StartedAt ?? Gateway.StartedAt;

    /// <summary>Longest first: the run that got furthest is almost always the one worth replaying.</summary>
    public int MessageCount => (Login?.Messages.Count ?? 0) + Gateway.Messages.Count;

    public override string ToString() =>
        $"run #{Index} {Gateway.Remote} — {Gateway.Messages.Count} gateway message(s) over {Gateway.Duration.TotalSeconds:F1}s"
        + (Login is null ? " (no login link)" : string.Empty);
}

/// <summary>Turns a capture file into sessions and runs.</summary>
public static class CaptureSessionReader
{
    /// <summary>Above this, only a prefix of a message is kept. See <see cref="CaptureMessage"/>.</summary>
    public const int FullByteLimit = 64 * 1024;

    /// <summary>How many bytes of an over-long message are kept, enough for any signature.</summary>
    public const int PrefixBytes = 32;

    /// <summary>Every session in the file, in the order their first line appears.</summary>
    public static IReadOnlyList<CaptureSession> ReadSessions(string path)
    {
        var byRemote = new Dictionary<string, CaptureSession>(StringComparer.Ordinal);
        var order = new List<CaptureSession>();

        foreach (CaptureLine line in CaptureFile.Read(path))
        {
            if (!byRemote.TryGetValue(line.Remote, out CaptureSession? session))
            {
                session = new CaptureSession(line.Remote, line.Protocol);
                session.StartedAt = line.At;
                byRemote[line.Remote] = session;
                order.Add(session);
            }

            session.EndedAt = line.At;

            if (line.Direction == CaptureDirection.SessionRequest)
            {
                ApplySessionRequest(session, line.SessionText);
                continue;
            }

            if (line.Direction == CaptureDirection.ClientToServerRaw)
            {
                // The ciphertext line is the decrypted line's twin; replay works from the
                // plaintext, so it is counted for timing only.
                continue;
            }

            // Exact literals emitted by FilePacketRecorder.RecordMessage. These are
            // metadata, not packet bytes; all other non-hex input still fails closed.
            if (line.Hex is "[private social payload redacted]" or "[hosted key redacted]")
            {
                session.RedactedMessages++;
                continue;
            }
            session.Add(Materialise(session, line));
        }

        return order;
    }

    /// <summary>
    /// Pairs each gateway session with the most recent login session that started before it. The
    /// client opens the two links in that order and the recorder writes them interleaved on one
    /// file, so file order is the only association available — the capture format records no
    /// account id, and the gateway ticket is not echoed on the login link.
    /// </summary>
    public static IReadOnlyList<CaptureRun> ReadRuns(string path)
    {
        var runs = new List<CaptureRun>();
        CaptureSession? pendingLogin = null;

        foreach (CaptureSession session in ReadSessions(path))
        {
            if (session.Link == CaptureLink.Login)
            {
                pendingLogin = session;
                continue;
            }

            runs.Add(new CaptureRun(path, runs.Count, pendingLogin, session));
            pendingLogin = null;
        }

        return runs;
    }

    /// <summary>The run that carries the most messages — the attempt that got furthest.</summary>
    public static CaptureRun LongestRun(string path)
    {
        IReadOnlyList<CaptureRun> runs = ReadRuns(path);
        if (runs.Count == 0)
        {
            throw new InvalidOperationException($"'{path}' contains no ExternalGatewayApi_3 session.");
        }

        return runs.MaxBy(r => r.MessageCount)!;
    }

    private static CaptureMessage Materialise(CaptureSession session, CaptureLine line)
    {
        bool truncated = line.Length > FullByteLimit;
        int hexChars = truncated ? PrefixBytes * 2 : line.Hex.Length;
        hexChars = Math.Min(hexChars, line.Hex.Length) & ~1;

        byte[] bytes = Convert.FromHexString(line.Hex.AsSpan(0, hexChars));
        PacketSignature signature = session.Link == CaptureLink.Login
            ? PacketSignature.ForLogin(bytes)
            : PacketSignature.ForGateway(bytes);

        return new CaptureMessage(line.At - session.StartedAt, line.Direction, line.Length, bytes, truncated, signature);
    }

    private static void ApplySessionRequest(CaptureSession session, string text)
    {
        foreach (string field in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = field.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            string name = field[..equals];
            ReadOnlySpan<char> value = field.AsSpan(equals + 1);

            switch (name)
            {
                case "crc" when uint.TryParse(value, out uint crc):
                    session.CrcLength = crc;
                    break;
                case "udp" when uint.TryParse(value, out uint udp):
                    session.UdpLength = udp;
                    break;
                case "id" when uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id):
                    session.SessionId = id;
                    break;
            }
        }
    }
}
