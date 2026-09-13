using Cranberry.Protocol;
using Cranberry.Transport;

namespace Cranberry.Zone.World;

/// <summary>
/// The bridge between one <c>SoeConnection</c> and one <see cref="Match"/>. This is the ONLY type
/// under <c>World/</c> that names a transport type; <c>WorldSeamTests</c> asserts that, and that
/// rule is what makes the later move into a socket-free assembly (migration step 9) a file move
/// rather than a refactor.
/// <para>
/// Transport-thread affine: every method here runs on the gateway listener thread, which under
/// migration step 1 is also the thread the match ticks on (<c>SoeListener.Post</c>, docs/22 §5.1).
/// </para>
/// </summary>
public sealed class SessionBridge : IPlayerSink
{
    private readonly IPacketRecorder? _recorder;
    private PacketWriter? _open;

    public SessionBridge(
        SoeConnection connection,
        ulong accountGuid,
        string characterName,
        IPacketRecorder? recorder = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        Connection = connection;
        AccountGuid = accountGuid;
        CharacterName = characterName;
        _recorder = recorder;
    }

    public SoeConnection Connection { get; }

    /// <summary>The login host's roster character guid.</summary>
    public ulong AccountGuid { get; }

    public string CharacterName { get; }

    public Match? Match { get; private set; }

    public MatchPlayer? Player { get; private set; }

    public ObserverView? View => Player?.View;

    public long MessagesSent { get; private set; }

    public bool IsOpen => Connection.State == ConnectionState.Open;

    public void AttachToMatch(Match match, MatchPlayer player)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(player);

        Match = match;
        Player = player;
        player.Sink = this;
    }

    public void DetachFromMatch()
    {
        if (Player is MatchPlayer player && ReferenceEquals(player.Sink, this))
        {
            player.Sink = null;
        }

        Match = null;
        Player = null;
    }

    /// <summary>Copies the payload into the match's arena and enqueues one command.</summary>
    public bool PostToMatch(CommandKind kind, EntityId target, uint value, ReadOnlySpan<byte> payload)
    {
        if (Match is not Match match || Player is not MatchPlayer player)
        {
            return false;
        }

        return match.Post(
            new Command(kind, player.Slot, target, value, PayloadOffset: 0, PayloadLength: 0),
            payload);
    }

    /// <summary>
    /// Starts a tunnel message with the gateway header already in place, so the caller writes only
    /// the zone body and there is no inner/outer writer pair and no intermediate array — the three
    /// allocations per packet <c>ZoneService.SendTunnel</c> costs today (docs/22 §12 row 3).
    /// The rent itself goes away with <c>PacketWriter.Reset()</c> in migration step 2.
    /// </summary>
    public PacketWriter Begin(byte channel = 0)
    {
        if (_open is not null)
        {
            throw new InvalidOperationException("Begin was called twice without an End.");
        }

        var writer = new PacketWriter();
        writer.WriteByte(new GatewayHeader(GatewayTunnelToClient.Opcode, channel).ToByte());
        _open = writer;
        return writer;
    }

    public void End()
    {
        if (_open is not PacketWriter writer)
        {
            throw new InvalidOperationException("End was called without a matching Begin.");
        }

        _open = null;
        Dispatch(writer);
    }

    public void SendRaw(byte channel, ReadOnlySpan<byte> body)
    {
        var writer = new PacketWriter(body.Length + 1);
        writer.WriteByte(new GatewayHeader(GatewayTunnelToClient.Opcode, channel).ToByte());
        writer.WriteRaw(body);
        Dispatch(writer);
    }

    /// <summary>
    /// A no-op today: <c>OutboundChannel</c> emits one retained reliable datagram per message and
    /// has no aggregation at all. This is the hook the <c>MultiBatcher</c> of migration step 6 fills
    /// (docs/22 §12 row 1).
    /// </summary>
    public void Flush()
    {
    }

    private void Dispatch(PacketWriter writer)
    {
        using (writer)
        {
            if (!IsOpen)
            {
                return;
            }

            _recorder?.RecordMessage(Connection, "s2c", writer.Written);
            Connection.Send(writer.Written);
            MessagesSent++;
        }
    }
}
