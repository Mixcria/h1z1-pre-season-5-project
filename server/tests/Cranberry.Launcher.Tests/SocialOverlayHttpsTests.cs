using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Cranberry.Launcher.Core;
using Cranberry.Launcher.Service;
using Cranberry.Login;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Match;

namespace Cranberry.Launcher.Tests;

public sealed class SocialOverlayHttpsTests
{
    [Fact]
    public async Task PasswordChangesRequireAuthenticationAndCurrentPasswordAndInvalidateOtherSignIns()
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-password-https-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var options = new LauncherHostOptions { Port = port, ProximityVoiceEnabled = false };
        File.WriteAllText(Path.Combine(root, "launcher-host.json"), JsonSerializer.Serialize(options));
        var log = new Recorder(); var zone = new ZoneService(log, log, new GatewayTicketRegistry(), new ZoneOptions()) { Post = a => a() };
        try
        {
            await using var host = new LauncherHost(root, new LocalAccountDirectory("owner"), zone, 20042, 20043);
            await host.StartAsync();
            using var http = LauncherConnection.CreateHttp(new() { ServerUrl = $"https://localhost:{port}/", CertificateSha256 = host.CertificateSha256 });
            const string oldPassword = "fixture-password-123", newPassword = "maple orbit velvet harbour";
            using var anonymous = await http.PostAsJsonAsync("api/account/password", new ChangePasswordRequest(oldPassword, newPassword));
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using var registration = await http.PostAsJsonAsync("api/register", new Credentials("Alice", oldPassword, options.JoinCode));
            registration.EnsureSuccessStatusCode(); var account = (await registration.Content.ReadFromJsonAsync<AuthSession>())!;
            using var another = await http.PostAsJsonAsync("api/login", new Credentials("Alice", oldPassword));
            another.EnsureSuccessStatusCode(); var otherSession = (await another.Content.ReadFromJsonAsync<AuthSession>())!;
            http.DefaultRequestHeaders.Authorization = new("Bearer", account.Token);
            using var wrong = await http.PostAsJsonAsync("api/account/password", new ChangePasswordRequest("wrong", newPassword));
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            using var weak = await http.PostAsJsonAsync("api/account/password", new ChangePasswordRequest(oldPassword, "Password123"));
            Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
            using var changed = await http.PostAsJsonAsync("api/account/password", new ChangePasswordRequest(oldPassword, newPassword));
            changed.EnsureSuccessStatusCode(); Assert.True((await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());
            Assert.Equal(account.AccountId, (await http.GetFromJsonAsync<LauncherState>("api/state"))!.Me.AccountId);
            http.DefaultRequestHeaders.Authorization = new("Bearer", otherSession.Token);
            using var revoked = await http.GetAsync("api/state"); Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            http.DefaultRequestHeaders.Authorization = null;
            using var oldLogin = await http.PostAsJsonAsync("api/login", new Credentials("Alice", oldPassword));
            Assert.Equal(HttpStatusCode.BadRequest, oldLogin.StatusCode);
            using var newLogin = await http.PostAsJsonAsync("api/login", new Credentials("Alice", newPassword));
            newLogin.EnsureSuccessStatusCode(); var current = (await newLogin.Content.ReadFromJsonAsync<AuthSession>())!;
            http.DefaultRequestHeaders.Authorization = new("Bearer", current.Token);
            for (int i = 0; i < 5; i++)
            {
                using var limited = await http.PostAsJsonAsync("api/account/password", new ChangePasswordRequest("wrong", oldPassword));
                Assert.Equal(i == 4 ? HttpStatusCode.TooManyRequests : HttpStatusCode.BadRequest, limited.StatusCode);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Recorder : ITransportLog, IPacketRecorder
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes) { }
    }

    [Fact]
    public async Task LauncherHttpsInvitesAndAcceptancePublishTheGameLobby()
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-party-https-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var options = new LauncherHostOptions { Port = port, ProximityVoiceEnabled = false };
        File.WriteAllText(Path.Combine(root, "launcher-host.json"), JsonSerializer.Serialize(options));
        var log = new Recorder(); var zone = new ZoneService(log, log, new GatewayTicketRegistry(), new ZoneOptions()) { Post = a => a() };
        var accounts = new LocalAccountDirectory("owner");
        var social = new SocialStore(Path.Combine(root, "social.json"), options.JoinCode, options.OwnerCode, accounts);
        var alice = social.Register(new("Alice", "fixture-password-123", options.JoinCode));
        var bob = social.Register(new("Bobby", "fixture-password-123", options.JoinCode));
        social.AddFriend(alice.AccountId, bob.Name);
        social.Respond(bob.AccountId, new(Assert.Single(social.State(bob.AccountId, []).Invites).Id, true));
        void Player(uint id, AuthSession session)
        {
            var request = new SessionRequest(3, id, 512, ZoneService.ProtocolName);
            var constructor = typeof(SoeConnection).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
            var connection = (SoeConnection)constructor.Invoke([new IPEndPoint(IPAddress.Loopback, 15000 + (int)id), request,
                new SessionSettings(), SessionDecision.Clear, zone, log, (Action<SoeConnection, ReadOnlyMemory<byte>>)((_, _) => { }), 0L, 32, null]);
            zone.OnConnected(connection);
            void Set(string name, object value) => connection.Tag!.GetType().GetProperty(name)!.SetValue(connection.Tag, value);
            Set("Authenticated", true); Set("Guid", (ulong)id); Set("AccountId", session.AccountId);
            Set("CharacterName", session.Name); Set("AppearanceReadySent", true); Set("SocialLinked", true);
            var sessions = (System.Collections.IDictionary)typeof(ZoneService).GetField("_accountSessions",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(zone)!;
            sessions.Add(connection, connection.Tag!);
        }
        Player(1, alice); Player(2, bob);
        try
        {
            await using var host = new LauncherHost(root, accounts, zone, 20042, 20043, social);
            await host.StartAsync();
            using var http = LauncherConnection.CreateHttp(new() { ServerUrl = $"https://localhost:{port}/", CertificateSha256 = host.CertificateSha256 });
            http.DefaultRequestHeaders.Authorization = new("Bearer", alice.Token);
            using var invite = await http.PostAsJsonAsync("api/party/invite", new TargetRequest(bob.AccountId));
            invite.EnsureSuccessStatusCode(); Assert.True((await invite.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());
            http.DefaultRequestHeaders.Authorization = new("Bearer", bob.Token);
            var state = (await http.GetFromJsonAsync<LauncherState>("api/state"))!;
            var pending = Assert.Single(state.Invites); Assert.Equal("GameParty", pending.Kind);
            using var accepted = await http.PostAsJsonAsync("api/invites/respond", new RespondRequest(pending.Id, true));
            accepted.EnsureSuccessStatusCode(); Assert.True((await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());
            state = (await http.GetFromJsonAsync<LauncherState>("api/state"))!;
            Assert.Empty(state.Invites); Assert.True(state.Lobby!.InGame);
            Assert.Equal(alice.AccountId, state.Lobby.LeaderId); Assert.Equal(2, state.Lobby.Members.Count);
            http.DefaultRequestHeaders.Authorization = new("Bearer", alice.Token);
            var leader = (await http.GetFromJsonAsync<LauncherState>("api/state"))!;
            Assert.Equal(state.Lobby.Id, leader.Lobby!.Id);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PinnedHttpsAndTheGameMailboxSharePrivateMessagesAndProfilePictures()
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-overlay-https-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var options = new LauncherHostOptions { Port = port, ProximityVoiceEnabled = false };
        File.WriteAllText(Path.Combine(root, "launcher-host.json"), JsonSerializer.Serialize(options));
        var log = new Recorder(); var zone = new ZoneService(log, log, new GatewayTicketRegistry(), new ZoneOptions()) { Post = a => a() };
        try
        {
            await using var host = new LauncherHost(root, new LocalAccountDirectory("owner"), zone, 20042, 20043);
            await host.StartAsync();
            using var http = LauncherConnection.CreateHttp(new() { ServerUrl = $"https://localhost:{port}/", CertificateSha256 = host.CertificateSha256 });
            async Task<T> Post<T>(string route, object body)
            {
                using var response = await http.PostAsJsonAsync(route, body); response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<T>())!;
            }
            async Task<string> Overlay(string actor, string action, string target = "", string text = "", string id = "")
            {
                var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                Assert.True(zone.SocialOverlaySubmit!(new SocialOverlayRequest(actor, action, target, text, id, s => response.SetResult(s))));
                return await response.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            using var missing = await http.PostAsJsonAsync("api/profile/avatar", new AvatarRequest(""));
            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
            var a = await Post<AuthSession>("api/register", new Credentials("Alice", "test-password-123", options.JoinCode));
            var b = await Post<AuthSession>("api/register", new Credentials("Bobby", "test-password-123", options.JoinCode));
            var e = await Post<AuthSession>("api/register", new Credentials("Evelyn", "test-password-123", options.JoinCode));
            http.DefaultRequestHeaders.Authorization = new("Bearer", a.Token);
            await Post<JsonElement>("api/friends", new TargetRequest(b.Name));
            http.DefaultRequestHeaders.Authorization = new("Bearer", b.Token);
            var state = (await http.GetFromJsonAsync<LauncherState>("api/state"))!;
            await Post<JsonElement>("api/invites/respond", new RespondRequest(Assert.Single(state.Invites).Id, true));
            http.DefaultRequestHeaders.Authorization = new("Bearer", a.Token);
            string pixels = string.Concat(Enumerable.Repeat("AABBCC", 48 * 48));
            var picture = await Post<AvatarView>("api/profile/avatar", new AvatarRequest(pixels));
            Assert.Equal(picture.Version, zone.SocialDirectoryProvider!().Avatar(a.AccountId)!.Version);
            var sent = await Post<DirectMessage>("api/messages", new MessageRequest(b.AccountId, "Hello | ; Ω <b>", Guid.NewGuid().ToString("N")));
            Assert.Equal(a.AccountId, sent.From);
            Assert.Contains(";F|" + a.AccountId + "|Alice|", await Overlay(b.AccountId, "state"));
            Assert.Contains(Uri.EscapeDataString(sent.Text), await Overlay(b.AccountId, "history", a.AccountId));
            Assert.Equal("D|" + sent.ClientId + "|" + sent.Id, await Overlay(a.AccountId, "send", b.AccountId, sent.Text, sent.ClientId));
            http.DefaultRequestHeaders.Authorization = new("Bearer", b.Token);
            Assert.Single((await http.GetFromJsonAsync<Conversation>("api/messages/" + a.AccountId))!.Messages);
            Assert.Equal(picture, await http.GetFromJsonAsync<AvatarView>("api/profile/" + a.AccountId + "/avatar"));
            Assert.Equal(1, Assert.Single((await http.GetFromJsonAsync<MessageUnread[]>("api/messages/unread"))!).Count);
            await Overlay(b.AccountId, "read", a.AccountId, sent.Id.ToString());
            Assert.Empty((await http.GetFromJsonAsync<MessageUnread[]>("api/messages/unread"))!);
            http.DefaultRequestHeaders.Authorization = new("Bearer", e.Token);
            using var denied = await http.GetAsync("api/messages/" + a.AccountId); Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            using var deniedAvatar = await http.GetAsync("api/profile/" + a.AccountId + "/avatar"); Assert.Equal(HttpStatusCode.BadRequest, deniedAvatar.StatusCode);
            http.DefaultRequestHeaders.Authorization = new("Bearer", a.Token);
            await Post<JsonElement>("api/friends/remove", new TargetRequest(b.AccountId));
            Assert.StartsWith("E|", await Overlay(b.AccountId, "history", a.AccountId));
            using var badPicture = await http.PostAsJsonAsync("api/profile/avatar", new AvatarRequest("not pixels"));
            Assert.Equal(HttpStatusCode.BadRequest, badPicture.StatusCode);
        }
        finally { Directory.Delete(root, true); }
    }
}
