using System.Net;
using Cranberry.Protocol;
using Cranberry.Transport;

namespace Cranberry.Login;

/// <summary>
/// The login service: owns every LoginUdp link. Packets whose layout is not yet measured are
/// recorded through <see cref="IPacketRecorder"/> so nothing the client says is lost.
/// </summary>
public sealed class LoginService : ISoeService
{
    public const ulong PrimaryServerId = 1;
    public const string PrimaryCountryCode = "GB";

    private readonly ITransportLog _log;
    private readonly IPacketRecorder _recorder;
    private readonly byte[] _key;
    private readonly CharacterRosterStore _characters;
    private readonly GatewayTicketRegistry _gatewayTickets;
    private readonly string _gatewayAddress;
    private readonly RegionNamingOptions _regions;
    private readonly LocalAccountDirectory? _accounts;
    private readonly Dictionary<SoeConnection, string> _launcherGateways = [];

    public LoginService(
        ITransportLog log,
        IPacketRecorder recorder,
        byte[] loginKey,
        CharacterRosterStore? characters = null,
        GatewayTicketRegistry? gatewayTickets = null,
        string gatewayAddress = "127.0.0.1:20043",
        RegionNamingOptions? regions = null,
        LocalAccountDirectory? accounts = null)
    {
        _log = log;
        _recorder = recorder;
        _key = loginKey;
        _characters = characters ?? new CharacterRosterStore();
        _gatewayTickets = gatewayTickets ?? new GatewayTicketRegistry();
        _gatewayAddress = gatewayAddress;
        _accounts = accounts;
        // docs/105 §9 (U-7). Read here rather than in the host so an embedder that never touches
        // the switch still gets the resolvable key, and a test can pin either form.
        _regions = regions ?? RegionNamingOptions.FromEnvironment();
    }

    /// <summary>docs/105 §9 — how this service names regions this run (for the boot banner).</summary>
    public RegionNamingOptions Regions => _regions;

    public event Action<SoeConnection, LoginRequest>? LoginRequested;

    public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request)
    {
        _recorder.RecordSession(remote, in request);
        if (!request.ProtocolName.StartsWith("LoginUdp_", StringComparison.Ordinal))
        {
            _log.Warn($"{remote} asked for '{request.ProtocolName}' on the login port; refused");
            return SessionDecision.Refuse;
        }

        return SessionDecision.Encrypted(_key);
    }

    public void OnConnected(SoeConnection connection)
    {
        _log.Info($"{connection} login link open");
        connection.RawInboundSink = (c, cipher, position) =>
            _recorder.RecordRaw(c, position, cipher);
    }

    public void OnMessage(SoeConnection connection, Span<byte> message)
    {
        _recorder.RecordMessage(connection, "c2s", message);
        if (message.IsEmpty)
        {
            return;
        }

        byte opcode = message[0];
        ReadOnlySpan<byte> body = message.Slice(1);
        if (_accounts is not null && opcode != LoginRequest.Opcode && connection.Tag is not string)
        {
            _log.Warn($"{connection} refused unauthenticated login operation 0x{opcode:x2}");
            return;
        }
        try
        {
            switch (opcode)
            {
                case LoginRequest.Opcode:
                    LoginRequest request = LoginRequest.Parse(body);
                    if (_accounts is not null)
                    {
                        connection.Tag = null;
                        _launcherGateways.Remove(connection);
                        if (!_accounts.TryResolveConnection(request.Ticket, connection.RemoteEndPoint, out string accountId, out string? gatewayOverride))
                        {
                            using var denied = new PacketWriter();
                            new LoginReply { LoggedIn = false, Status = 0, ResultCode = 0 }.WriteTo(denied);
                            Send(connection, denied.Written);
                            return;
                        }
                        connection.Tag = accountId;
                        if (gatewayOverride is not null) _launcherGateways[connection] = gatewayOverride;
                    }
                    _log.Info($"{connection} LoginRequest accepted; fingerprint={request.Fingerprint.Length} chars");
                    LoginRequested?.Invoke(connection, request);
                    SendLoginReply(connection);
                    break;

                case CharacterCreateRequest.Opcode:
                    HandleCharacterCreate(connection, CharacterCreateRequest.Parse(body));
                    break;

                case CharacterLoginRequest.Opcode:
                    CharacterLoginRequest characterLogin = CharacterLoginRequest.Parse(body);
                    CharacterLoginContext context = CharacterLoginContext.Parse(characterLogin.Payload);
                    if ((_accounts is not null && !_accounts.Owns((string)connection.Tag!, characterLogin.EntityKey))
                        || !_characters.TryGet(characterLogin.EntityKey, characterLogin.ServerId, out CharacterEntry selected)
                        || selected.Status != CharacterEntry.StatusAvailable)
                    {
                        _log.Warn($"{connection} CharacterLoginRequest rejected: entity={characterLogin.EntityKey} server={characterLogin.ServerId} was not advertised as available");
                        SendCharacterLoginFailure(connection, characterLogin);
                        break;
                    }

                    _log.Info($"{connection} CharacterLoginRequest entity={characterLogin.EntityKey} server={characterLogin.ServerId} name='{selected.Name}' payload={characterLogin.Payload.Length} bytes {context}");
                    SendCharacterLoginSuccess(connection, characterLogin, context, selected);
                    break;

                case CharacterDeleteRequest.Opcode:
                {
                    // 09 <u64>: the character-select screen's DeleteCharacter(guid). Status 1 makes the
                    // client drop the row; the roster must not list the character afterwards.
                    CharacterDeleteRequest delete = CharacterDeleteRequest.Parse(body);
                    bool removed = (_accounts is null || _accounts.Owns((string)connection.Tag!, delete.EntityKey))
                        && _characters.Remove(delete.EntityKey);
                    using (var writer = new PacketWriter())
                    {
                        new CharacterDeleteReply(delete.EntityKey, removed ? CharacterDeleteReply.Success : CharacterDeleteReply.Failure).WriteTo(writer);
                        Send(connection, writer.Written);
                    }

                    _log.Info($"{connection} CharacterDeleteRequest entity={delete.EntityKey} → {(removed ? "deleted" : "unknown character, refused")}");
                    break;
                }

                case 0x03:
                    // One byte from the client when it closes the login link (relogin / shutdown); no reply.
                    _log.Info($"{connection} login opcode 0x03: client is closing the login link");
                    break;

                case 0x0B: // CharacterSelectInfoRequest (id from the client's dispatcher numbering; body observed on the wire)
                    _log.Info($"{connection} CharacterSelectInfoRequest body={Convert.ToHexString(body)}");
                    SendCharacterSelectInfo(connection);
                    break;

                case 0x0D: // ServerListRequest
                    _log.Info($"{connection} ServerListRequest body={Convert.ToHexString(body)}");
                    SendServerList(connection);
                    break;

                case TunnelAppPacketClientToServer.Opcode:
                    HandleTunnel(connection, TunnelAppPacketClientToServer.Parse(body));
                    break;

                default:
                    _log.Warn($"{connection} login opcode 0x{opcode:x2} ({message.Length} bytes) has no handler yet: {Convert.ToHexString(message.Slice(0, Math.Min(64, message.Length)))}");
                    break;
            }
        }
        catch (PacketFormatException ex)
        {
            _log.Warn($"{connection} login opcode 0x{opcode:x2}: {ex.Message}");
        }
    }

    public void OnDisconnected(SoeConnection connection, DisconnectCause cause)
    {
        _launcherGateways.Remove(connection);
        _log.Info($"{connection} login link closed ({cause})");
    }

    /// <summary>Sends one application packet on a login link and records it.</summary>
    public void Send(SoeConnection connection, ReadOnlySpan<byte> packet)
    {
        _recorder.RecordMessage(connection, "s2c", packet);
        connection.Send(packet);
    }

    private void SendServerList(SoeConnection connection)
    {
        // Europe is the one open login host (the seeded roster lives on server 1); the other
        // regions are listed but locked (State bit 0) and not allowed as a login host. Data-centre
        // codes are the client's own DataCenter.txt: 1 LVS, 2 AMS, 3 SAO, 4 SGP, 6 SYD, 7 ABE.
        // docs/105 §9 (U-7): Region is a locale KEY, not a code. `EU` is in neither the client's
        // CodeStringMappings nor its T4 names, so the client logged `t4lookup('EU') not found` every
        // five seconds; `CharacterCreate.RegionEu` is a CodeStringMappings row (string id 9579) that
        // resolves to the same two letters. See RegionNames.cs for the capture and the datasheet.
        var europe = new GameServerEntry
        {
            Id = PrimaryServerId,
            Name = "Europe",
            Region = _regions.Resolve("EU"),
            State = 0,
            Population = 0,
            // Mode 13 is the King of the Kill game mode id the queue UI keys on (the 2016 server's
            // Population document carried Mode="13"; QueueUpdateGameMode / the BR HUD use the same id).
            Info = new PopulationInfo { IsLogin = true, DataCenter = "AMS", Mode = 13 },
            IsAllowed = true,
        };
        static GameServerEntry Locked(ulong id, string name, string region, string dataCenter) => new()
        {
            Id = id,
            Name = name,
            Region = region,
            State = GameServerEntry.StateLocked,
            Population = 0,
            Info = new PopulationInfo { IsLogin = false, DataCenter = dataCenter, PopulationLocked = true },
            IsAllowed = false,
        };
        var reply = new ServerListReply(
        [
            europe,
            Locked(2, "North America", _regions.Resolve("US"), "LVS"),
            Locked(3, "South America", _regions.Resolve("SA"), "SAO"),
            Locked(4, "Asia", _regions.Resolve("AS"), "SGP"),
            Locked(5, "Australia", _regions.Resolve("AU"), "SYD"),
            .. GameWorldCatalog.Default.Where(world => world.WorldId != PrimaryServerId)
                .Select(world => world.ServerEntry(_regions)),
        ]);
        using var writer = new PacketWriter();
        reply.WriteTo(writer);
        Send(connection, writer.Written);
        _log.Info($"{connection} ServerListReply sent ({writer.Position} bytes, {reply.Servers.Count} servers; Europe/AMS login, Solo/Duos/Fives worlds and reserved private hosted slots, Region='{europe.Region}')");
    }

    private void SendCharacterSelectInfo(SoeConnection connection)
    {
        CharacterEntry[] characters = _characters.Snapshot()
            .Where(character => _accounts is null || _accounts.Owns((string)connection.Tag!, character.EntityKey))
            .ToArray();
        var reply = new CharacterSelectInfoReply(Status: 1, Flag: false, Characters: characters);
        using var writer = new PacketWriter();
        reply.WriteTo(writer);
        Send(connection, writer.Written);
        _log.Info($"{connection} CharacterSelectInfoReply sent ({writer.Position} bytes, {characters.Length} characters)");
    }

    private void SendLoginReply(SoeConnection connection)
    {
        // Account identity is resolved before this reply when the host supplies its local directory.
        // The direct-launch client has no LaunchPad country. Supplying the actual login reply's
        // IpCountryCode lets its own DataCenterCountryMapping select Europe/AMS cleanly instead
        // of defaulting to the locked North America (West) row.
        var reply = new LoginReply { IpCountryCode = PrimaryCountryCode };
        using var writer = new PacketWriter();
        reply.WriteTo(writer);
        Send(connection, writer.Written);
        _log.Info($"{connection} LoginReply sent ({writer.Position} bytes, loggedIn={reply.LoggedIn} "
            + $"status={reply.Status} country={reply.IpCountryCode})");
    }

    private void HandleCharacterCreate(
        SoeConnection connection,
        CharacterCreateRequest request)
    {
        CharacterCreatePayload payload = CharacterCreatePayload.Parse(request.Payload);
        if (request.ServerId != PrimaryServerId
            || !CharacterNamePolicy.TryNormalize(payload.Name, out string normalizedName))
        {
            _log.Warn($"{connection} CharacterCreateRequest refused: server={request.ServerId} "
                + $"name='{payload.Name}' is not valid for the Europe roster");
            SendCharacterCreateFailure(connection);
            return;
        }

        payload = payload with { Name = normalizedName };
        if (!_characters.TryCreateUnique(request.ServerId, payload, out CharacterEntry character))
        {
            _log.Warn($"{connection} CharacterCreateRequest refused: name='{normalizedName}' is already in use");
            SendCharacterCreateFailure(connection);
            return;
        }

        _log.Info($"{connection} CharacterCreateRequest server={request.ServerId} entity={character.EntityKey} {payload}");

        if (_accounts is not null)
        {
            try { _accounts.BindCharacter((string)connection.Tag!, character.EntityKey); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _characters.Remove(character.EntityKey);
                _log.Warn($"{connection} character account membership could not be saved: {ex.Message}");
                SendCharacterCreateFailure(connection);
                return;
            }
        }

        using var writer = new PacketWriter();
        new CharacterCreateReply(CharacterCreateReply.StatusSuccess, character.EntityKey).WriteTo(writer);
        Send(connection, writer.Written);
        _log.Info($"{connection} CharacterCreateReply sent ({writer.Position} bytes, entity={character.EntityKey})");
    }

    private void SendCharacterCreateFailure(SoeConnection connection)
    {
        using var writer = new PacketWriter();
        new CharacterCreateReply(CharacterCreateReply.StatusFailure, EntityKey: 0).WriteTo(writer);
        Send(connection, writer.Written);
        _log.Info($"{connection} CharacterCreateReply sent ({writer.Position} bytes, status=0)");
    }

    private void SendCharacterLoginSuccess(
        SoeConnection connection,
        CharacterLoginRequest request,
        CharacterLoginContext context,
        CharacterEntry character)
    {
        CharacterSelectionPayload appearance = CharacterSelectionPayload.Parse(character.Payload);
        GatewayAdmission admission = _gatewayTickets.Issue(
            request.EntityKey,
            appearance.Name,
            appearance.Gender,
            appearance.HeadId,
            appearance.HairId,
            appearance.SkinToneId,
            appearance.ProfileId,
            connection.Tag as string ?? string.Empty);
        byte[] payload = new GatewayConnectInfo(
            GatewayId: context.GatewayId,
            Address: _launcherGateways.GetValueOrDefault(connection, _gatewayAddress),
            Ticket: admission.Ticket,
            Key: admission.Key,
            CipherMode: GatewayConnectInfo.CipherRc4,
            Guid: request.EntityKey,
            Reserved: 0,
            Text10: string.Empty,
            Text11: string.Empty,
            Text12: string.Empty,
            FeatureBits: 0)
            .ToArray();

        using var writer = new PacketWriter();
        new CharacterLoginReply(
            EntityKey: request.EntityKey,
            ServerId: request.ServerId,
            Status: CharacterLoginReply.StatusSuccess,
            Payload: payload)
            .WriteTo(writer);
        Send(connection, writer.Written);
        _log.Info($"{connection} CharacterLoginReply sent ({writer.Position} bytes, "
            + $"entity={request.EntityKey} server={request.ServerId} "
            + $"gateway='{_launcherGateways.GetValueOrDefault(connection, _gatewayAddress)}' cipher=RC4 "
            + $"head={admission.HeadId} hair={admission.HairId} skinTone={admission.SkinToneId})");
    }

    private void SendCharacterLoginFailure(
        SoeConnection connection,
        CharacterLoginRequest request)
    {
        using var writer = new PacketWriter();
        new CharacterLoginReply(
            EntityKey: request.EntityKey,
            ServerId: request.ServerId,
            Status: 0,
            Payload: [])
            .WriteTo(writer);
        Send(connection, writer.Written);
        _log.Info($"{connection} CharacterLoginReply sent ({writer.Position} bytes, status=0)");
    }

    private void HandleTunnel(
        SoeConnection connection,
        TunnelAppPacketClientToServer tunnel)
    {
        ReadOnlySpan<byte> payload = tunnel.Payload;
        if (payload.Length >= 2
            && payload[0] == NameValidationRequest.Family
            && payload[1] == NameValidationRequest.SubOpcode)
        {
            NameValidationRequest request = NameValidationRequest.Parse(payload);
            _log.Info($"{connection} NameValidationRequest server={tunnel.ServerId} name='{request.Name}' context='{request.Context}'");

            uint result;
            if (tunnel.ServerId != PrimaryServerId
                || !CharacterNamePolicy.TryNormalize(request.Name, out string normalizedName))
            {
                result = NameValidationReply.InvalidName;
            }
            else if (_characters.ContainsName(normalizedName))
            {
                result = NameValidationReply.NameTaken;
            }
            else
            {
                result = NameValidationReply.Success;
            }

            // The client correlates the response with both original request strings, so preserve
            // them even though the authoritative create path stores the normalized name.
            using var inner = new PacketWriter();
            new NameValidationReply(request.Name, request.Context, result).WriteTo(inner);
            using var outer = new PacketWriter();
            new TunnelAppPacketServerToClient(tunnel.ServerId, inner.Written.ToArray())
                .WriteTo(outer);
            Send(connection, outer.Written);
            _log.Info($"{connection} NameValidationReply sent ({outer.Position} bytes, result={result})");
            return;
        }

        _log.Warn($"{connection} tunneled app packet for server {tunnel.ServerId} has no handler yet: "
            + Convert.ToHexString(payload.Slice(0, Math.Min(64, payload.Length))));
    }
}
