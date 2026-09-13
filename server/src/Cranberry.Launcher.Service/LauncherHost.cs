using System.Collections.Concurrent;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using System.Threading.RateLimiting;
using Cranberry.Launcher.Core;
using Cranberry.Launcher.Core.Voice;
using Cranberry.Login;
using Cranberry.Transport;
using Cranberry.Zone;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cranberry.Launcher.Service;

public sealed partial class LauncherHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly SocialStore _social;
    private readonly ConcurrentDictionary<string, byte> _tunnels = [];
    private readonly DoorClientReadiness _doorClients = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ZoneService _zone;
    private readonly VoiceRouter _voice;
    private Task? _worldPump;
    private IReadOnlyDictionary<string, string> _presence = new Dictionary<string, string>();
    public LauncherHostOptions Options { get; }
    public string CertificateSha256 { get; }
    public Action<string, byte, string, ReadOnlyMemory<byte>>? ObserveTunnelDatagram { get; set; }

    public LauncherHost(string root, LocalAccountDirectory accounts, ZoneService zone, int loginPort, int gatewayPort,
        SocialStore? social = null, SoeListener? loginListener = null, SoeListener? gatewayListener = null,
        bool enableDiagnostics = false)
    {
        _diagnostics = enableDiagnostics ? new LauncherDiagnostics() : null;
        if ((loginListener is null) != (gatewayListener is null))
            throw new ArgumentException("Both game listeners are required for an in-process tunnel.");
        zone.LocalOwnerAccountId = accounts.LocalAccountId;
        zone.CharacterSelectTicketFactory = account => accounts.IssueCharacterSelectTicket(account, gatewayPort);
        Options = LauncherHostOptions.Load(root);
        _zone = zone;
        _voice = new(Options.VoiceRangeMetres, Options.VoiceMaxSpeakers);
        var certificate = Options.Certificate(root);
        CertificateSha256 = certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
        _social = social ?? new(Path.Combine(root, "state", "launcher", "social.json"), Options.JoinCode, Options.OwnerCode, accounts);
        zone.SocialDirectoryProvider = () => _social.InGameDirectory;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = root });
        builder.Logging.ClearProviders(); // The game host owns logging; never log headers, tokens, or request bodies.
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = 16 * 1024;
            k.Listen(IPAddress.Parse(Options.BindAddress), Options.Port, listen => listen.UseHttps(certificate, https =>
                https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13));
        });
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            o.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 12, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(context.Items["account"] is string account ? "account:" + account
                    : "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "local"),
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 3000, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        _app = builder.Build();
        if (_diagnostics is { } diagnostics)
        {
            // The outer position observes statuses assigned by the existing error middleware.
            // An upgraded WebSocket remains active until its endpoint returns.
            _app.Use(async (context, next) =>
            {
                diagnostics.HttpStarted();
                bool faulted = false;
                try { await next(context); }
                catch { faulted = true; throw; }
                finally
                {
                    int status = faulted && !context.Response.HasStarted ? 500 : context.Response.StatusCode;
                    diagnostics.HttpCompleted(status, faulted);
                }
            });
        }
        _app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try { await next(context); }
            catch (Exception ex) when (!context.Response.HasStarted && ex is UnauthorizedAccessException or InvalidOperationException or InvalidDataException or ArgumentException)
            {
                context.Response.StatusCode = ex is UnauthorizedAccessException ? 401 : 400;
                await context.Response.WriteAsJsonAsync(new ApiError(ex.Message));
            }
        });
        // Authenticated idle launchers behind one NAT each get their own request budget.
        // Invalid credentials still share the unauthenticated IP budget.
        _app.Use(async (context, next) =>
        {
            if (context.Request.Headers.Authorization.Count > 0)
            {
                try { context.Items["account"] = await Locked(() => _social.Authenticate(Token(context))); }
                catch (UnauthorizedAccessException) { }
            }
            await next(context);
        });
        _app.UseRateLimiter();
        _app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15), KeepAliveTimeout = TimeSpan.FromSeconds(30) });
        MapSocialOverlay();
        MapLeaderboard();
        LauncherUpdateFeed.Map(_app, root);
        DownloadConfigurationFeed.Map(_app, root);

        string package = Path.GetFullPath(Path.Combine(root, Options.PackageDirectory));
        var manifestGate = new object();
        GameManifest? cachedManifest = null;
        DateTime manifestStamp = default;
        GameManifest Manifest()
        {
            lock (manifestGate)
            {
                string path = Path.Combine(package, "manifest.json");
                if (!File.Exists(path)) throw new InvalidOperationException("Game package is not published yet. Run Publish-Game.ps1 on the host.");
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (cachedManifest is not null && stamp == manifestStamp) return cachedManifest;
                var manifest = JsonSerializer.Deserialize<GameManifest>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                GameInstaller.Validate(manifest);
                cachedManifest = manifest; manifestStamp = stamp;
                return manifest;
            }
        }
        _app.MapGet("/health", () => new { Status = "ok", ClientVersion = "0.0.118.208059", EncryptedTunnel = true });
        _app.MapPost("/api/register", async (Credentials request) => await Locked(() => _social.Register(request))).RequireRateLimiting("auth");
        _app.MapPost("/api/login", async (Credentials request) => await Locked(() => _social.Login(request))).RequireRateLimiting("auth");
        _app.MapPost("/api/account/password", async (HttpContext c, ChangePasswordRequest r) =>
            await Locked(() => { _social.ChangePassword(Token(c), r); return new { Ok = true }; })).RequireRateLimiting("auth");
        _app.MapPost("/api/logout", async (HttpContext c) =>
        {
            // An explicit return avoids the RequestDelegate overload, which discards the JSON result.
            return await Locked(() => { _social.Logout(Token(c)); return new { Ok = true }; });
        });
        _app.MapGet("/api/state", async (HttpContext c) => { return await SocialState(c); });
        _app.MapGet("/api/manifest", async (HttpContext c) => { await Actor(c); return Manifest(); });
        _app.MapGet("/api/content/{hash}", async (HttpContext c, string hash) =>
        {
            await Actor(c);
            if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)
                || !Manifest().Files.Any(f => f.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase))) return Results.NotFound();
            string path = Path.Combine(package, "content", hash.ToUpperInvariant());
            return File.Exists(path) ? Results.File(path, "application/octet-stream", enableRangeProcessing: true) : Results.NotFound();
        });
        _app.MapPost("/api/friends", async (HttpContext c, TargetRequest r) => await Act(c, a => _social.AddFriend(a, r.Target)));
        _app.MapPost("/api/friends/remove", async (HttpContext c, TargetRequest r) => await Act(c, a => _social.RemoveFriend(a, r.Target)));
        _app.MapPost("/api/party/invite", async (HttpContext c, TargetRequest r) => await InviteParty(c, r));
        _app.MapPost("/api/invites/respond", async (HttpContext c, RespondRequest r) => await RespondParty(c, r));
        _app.MapPost("/api/party/ready", async (HttpContext c, ReadyRequest r) => await Act(c, a => _social.Ready(a, r.Ready)));
        _app.MapPost("/api/party/mode", async (HttpContext c, ModeRequest r) => await Act(c, a => _social.Mode(a, r.Mode)));
        _app.MapPost("/api/party/queue", async (HttpContext c) =>
        {
            long waitStarted = DiagnosticTimestamp();
            await _gate.WaitAsync(c.RequestAborted);
            long acquired = DiagnosticTimestamp();
            try
            {
                string actor = _social.Authenticate(Token(c));
                var party = _social.Queue(actor);
                string? error = await OnZone(zone, () => zone.LauncherQueue(party.Accounts, party.Mode), c.RequestAborted);
                if (error is not null) throw new InvalidOperationException(error);
                return new { Ok = true };
            }
            finally { ReleaseGate(waitStarted, acquired); }
        });
        _app.MapPost("/api/party/leave", async (HttpContext c) =>
        {
            long waitStarted = DiagnosticTimestamp();
            await _gate.WaitAsync(c.RequestAborted);
            long acquired = DiagnosticTimestamp();
            try
            {
                string actor = _social.Authenticate(Token(c));
                string? error = await OnZone(zone, () => zone.LauncherCancel(actor), c.RequestAborted);
                if (error is not null) throw new InvalidOperationException(error);
                _social.Leave(actor);
                return new { Ok = true };
            }
            finally { ReleaseGate(waitStarted, acquired); }
        });
        _app.MapPost("/api/launch", async (HttpContext c, LaunchRequest request) =>
        {
            string actor = await Actor(c);
            DoorClientReadiness.RequireProtocol(request.DoorSwingProtocol);
            if (!_tunnels.ContainsKey(actor)) throw new InvalidOperationException("Open the encrypted game tunnel first.");
            string ticket = accounts.IssueLauncherTicket(actor, request.GatewayPort);
            _doorClients.Begin(actor, ticket, request.DoorSwingProtocol);
            return new GameLaunch(ticket, "127.0.0.1:" + loginPort, Manifest().BuildId);
        });
        zone.DoorSwingClientReady = _doorClients.IsReady;
        _app.MapPost("/api/client/doors-ready", async (HttpContext c, DoorClientReadyRequest request) =>
        {
            string actor = await Actor(c);
            if (!_tunnels.ContainsKey(actor) || string.IsNullOrWhiteSpace(request.Ticket)
                || !_doorClients.Confirm(actor, request.Ticket, request.DoorSwingProtocol))
                throw new InvalidOperationException("The game launch changed. Close the game and try Play again.");
            return new { Ok = true };
        });
        _app.MapGet("/api/tunnel", async (HttpContext c) =>
        {
            if (!c.WebSockets.IsWebSocketRequest) { c.Response.StatusCode = 400; return; }
            string actor = await Actor(c);
            if (!_tunnels.TryAdd(actor, 0)) throw new InvalidOperationException("This account already has an active game tunnel.");
            try { await UdpTunnelEndpoint.Run(c, actor, accounts, loginPort, gatewayPort,
                ObserveTunnelDatagram is { } observer ? (channel, stage, bytes) => observer(actor, channel, stage, bytes) : null,
                loginListener, gatewayListener, _diagnostics); }
            finally { _doorClients.Remove(actor); _tunnels.TryRemove(actor, out _); }
        });
        _app.MapGet("/api/voice/proximity", async (HttpContext c) =>
        {
            string actor = await Actor(c);
            if (!Options.ProximityVoiceEnabled) { c.Response.StatusCode = 404; return; }
            if (!c.WebSockets.IsWebSocketRequest) { c.Response.StatusCode = 400; return; }
            VoicePeer peer = _voice.Connect(actor);
            try
            {
                using var socket = await c.WebSockets.AcceptWebSocketAsync();
                await ProximityVoiceEndpoint.Run(socket, _voice, peer, c.RequestAborted);
            }
            finally { _voice.Disconnect(peer); }
        });
    }

    private static string Token(HttpContext c)
    {
        string value = c.Request.Headers.Authorization.ToString();
        if (!value.StartsWith("Bearer ", StringComparison.Ordinal) || value.Length != 71)
            throw new UnauthorizedAccessException("Sign in first.");
        return value[7..];
    }
    private Task<string> Actor(HttpContext c) => c.Items["account"] is string actor
        ? Task.FromResult(actor) : Locked(() => _social.Authenticate(Token(c)));
    private async Task<object> Act(HttpContext c, Action<string> action) => await Locked(() =>
    { action(_social.Authenticate(Token(c))); return (object)new { Ok = true }; });
    private async Task<T> Locked<T>(Func<T> work)
    {
        long waitStarted = DiagnosticTimestamp();
        await _gate.WaitAsync();
        long acquired = DiagnosticTimestamp();
        try { return work(); } finally { ReleaseGate(waitStarted, acquired); }
    }
    public static async Task<T> OnZone<T>(ZoneService zone, Func<T> work, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var registration = timeout.Token.Register(() => completion.TrySetCanceled(timeout.Token));
        (zone.Post ?? throw new InvalidOperationException("Game listener is not ready."))(() =>
        {
            if (completion.Task.IsCompleted) return;
            try { completion.TrySetResult(work()); } catch (Exception ex) { completion.TrySetException(ex); }
        });
        return await completion.Task;
    }
    private async Task PublishWorld()
    {
        long nextPresence = 0;
        bool hadVoice = false;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            do
            {
                bool presenceDue = Environment.TickCount64 >= nextPresence;
                bool hasVoice = Options.ProximityVoiceEnabled && _voice.ConnectionCount > 0;
                bool voiceDue = hasVoice || hadVoice;
                hadVoice = hasVoice;
                if (!presenceDue && !voiceDue) continue;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(400);
                try
                {
                    var hud = _voice.HudViews(Environment.TickCount64)
                        .Select(v => new ProximityVoiceHud(v.AccountId, v.CharacterId, v.MatchId, v.Speakers)).ToArray();
                    var snapshot = await OnZone(_zone, () =>
                    {
                        if (voiceDue) _zone.PublishProximityVoiceHud(hud, _voice.RangeMetres, Environment.TickCount64);
                        return (Presence: presenceDue ? _zone.LauncherPresence() : null,
                            Voice: voiceDue ? _zone.ProximityVoicePlayers() : null);
                    }, deadline.Token);
                    if (snapshot.Presence is { } presence)
                    {
                        // Duplicate account sessions are deliberately not reported as a usable presence.
                        Volatile.Write(ref _presence, presence.GroupBy(p => p.AccountId)
                            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First().Status));
                        nextPresence = Environment.TickCount64 + 1000;
                    }
                    if (snapshot.Voice is { } voice)
                        _voice.UpdateWorld(voice.Select(p => new VoiceParticipant(p.AccountId, p.CharacterId,
                            p.MatchId, p.Position, p.Heading)).ToArray(), Environment.TickCount64);
                }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
                {
                    _voice.UpdateWorld([], Environment.TickCount64);
                    Volatile.Write(ref _presence, new Dictionary<string, string>());
                }
            } while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _app.StartAsync(ct);
        _worldPump = PublishWorld();
        _overlayPump = ProcessOverlay();
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _overlayRequests.Writer.TryComplete();
        if (_overlayPump is not null) await _overlayPump;
        if (_worldPump is not null) await _worldPump;
        _voice.Close();
        await _app.StopAsync(); await _app.DisposeAsync(); _gate.Dispose(); _stop.Dispose();
    }
}
