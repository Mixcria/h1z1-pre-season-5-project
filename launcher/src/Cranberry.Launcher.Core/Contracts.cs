namespace Cranberry.Launcher.Core;

public sealed record Credentials(string Name, string Password, string JoinCode = "");
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record AuthSession(string Token, string AccountId, string Name);
public sealed record Person(string AccountId, string Name, bool Online, string GameStatus = "Offline", string AvatarVersion = "");
public sealed record SocialInvite(string Id, string FromId, string FromName, string Kind);
public sealed record LobbyMember(string AccountId, string Name, bool Ready, bool Online, string GameStatus);
public sealed record LobbyView(string Id, string LeaderId, string Mode, IReadOnlyList<LobbyMember> Members, bool InGame = false);
public sealed record LauncherState(Person Me, IReadOnlyList<Person> Friends, IReadOnlyList<SocialInvite> Invites,
    LobbyView? Lobby);
public sealed record TargetRequest(string Target);
public sealed record AvatarRequest(string Pixels);
public sealed record AvatarView(string Version, string Pixels);
public sealed record MessageRequest(string Target, string Text, string ClientId);
public sealed record ReadMessagesRequest(string Target, long ThroughId);
public sealed record DirectMessage(long Id, string ClientId, string From, string To, string Text, DateTimeOffset SentAt);
public sealed record Conversation(string AccountId, IReadOnlyList<DirectMessage> Messages, long ReadThrough);
public sealed record MessageUnread(string AccountId, int Count);
public sealed record RespondRequest(string Id, bool Accept);
public sealed record ReadyRequest(bool Ready);
public sealed record ModeRequest(string Mode);
public sealed record GameLaunch(string Ticket, string LoginAddress, string BuildId);
public sealed record LaunchRequest(int GatewayPort, int DoorSwingProtocol = 0);
public sealed record DoorClientReadyRequest(string Ticket, int DoorSwingProtocol);
public sealed record ApiError(string Error);
public sealed record GameFile(string Path, long Size, string Sha256);
public sealed record GameManifest(int Version, string BuildId, string ClientVersion, IReadOnlyList<GameFile> Files);
public sealed record InstallProgress(string Message, long Complete, long Total);

public sealed record LauncherSettings
{
    public string ServerUrl { get; init; } = "https://localhost:20040/";
    public string CertificateSha256 { get; init; } = "";
    // Optional local override. Otherwise the trusted API supplies the current content origin.
    public string ContentBaseUrl { get; init; } = "";
    public int ConcurrentDownloads { get; init; } = 4;
    public bool TransportDiagnostics { get; init; }
    public string InstallDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cranberry", "Game");
    public string JoinCode { get; init; } = "";
    public string Name { get; init; } = "";
    public bool ProximityVoiceEnabled { get; init; } = true;
    public int VoiceInputDevice { get; init; } = -1;
    public int VoiceOutputDevice { get; init; } = -1;
    public int VoiceVolume { get; init; } = 80;
    public string VoicePushToTalkKey { get; init; } = "";

    public static Uri ValidateServer(string address)
    {
        if (!Uri.TryCreate(address.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("Enter a valid server URL.");
        if (uri.Scheme == "http" && !uri.IsLoopback)
            throw new InvalidDataException("Use HTTPS for a server on another computer.");
        return uri;
    }

    public static Uri? ValidateContentBase(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        if (!address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || address.Contains('\\') || address.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
            || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("ContentBaseUrl must be an HTTPS directory URL without credentials, query or fragment.");
        // Uri.UserInfo is empty for https://@host/, but that is still a user-info authority.
        int end = address.IndexOf('/', "https://".Length);
        if (address.AsSpan("https://".Length, (end < 0 ? address.Length : end) - "https://".Length).Contains('@'))
            throw new InvalidDataException("ContentBaseUrl cannot contain credentials.");
        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }
}
