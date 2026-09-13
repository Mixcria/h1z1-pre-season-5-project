using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Cranberry.Launcher.Core;

namespace Cranberry.NetworkBots;

internal sealed class CloudAccess : IAsyncDisposable
{
    public readonly List<(HttpClient Http, GameTunnel Tunnel, GameLaunch Launch, string Name)> Players = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private sealed record SavedAccount(string Name, string Password, bool Registered);
    public static async Task<CloudAccess> OpenFixture(TlsFixtureAddress fixture, int count, CancellationToken ct)
    {
        var result = new CloudAccess();
        try
        {
            var players = new (HttpClient Http, GameTunnel Tunnel, GameLaunch Launch, string Name)[count];
            using var slots = new SemaphoreSlim(24);
            await Task.WhenAll(Enumerable.Range(0, count).Select(async i =>
            {
                await slots.WaitAsync(ct);
                try
                {
                    var player = await TlsFixture.Connect(fixture, i, ct);
                    players[i] = (player.Http, player.Tunnel, player.Launch, $"TlsBot{i:D5}");
                    lock (result.Players) result.Players.Add(players[i]);
                }
                finally { slots.Release(); }
            }));
            result.Players.Clear(); result.Players.AddRange(players);
            Console.WriteLine($"Connected {count} loopback TLS gameplay tunnels.");
            return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }

    public static async Task<CloudAccess> Open(string profile, string accountsFile, int count, bool prepareOnly, CancellationToken ct)
    {
        // Uses only the public distributed profile, never owner credentials or server state.
        var settings = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(profile))
            ?? throw new InvalidDataException("Invalid public profile");
        var saved = File.Exists(accountsFile)
            ? JsonSerializer.Deserialize<List<SavedAccount>>(File.ReadAllText(accountsFile), Json)! : [];
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(accountsFile))!);
        for (int i = saved.Count; i < count; i++)
            saved.Add(new("NetQA_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(5)),
                Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), false));
        File.WriteAllText(accountsFile, JsonSerializer.Serialize(saved, Json));
        var result = new CloudAccess();
        try
        {
            for (int i = 0; i < count; i++)
            {
                var account = saved[i];
                var http = LauncherConnection.CreateHttp(settings);
                GameTunnel? tunnel = null;
                try
                {
                    using var health = await http.GetAsync("health", ct);
                    health.EnsureSuccessStatusCode();
                    AuthSession session;
                    using (var response = await http.PostAsJsonAsync(account.Registered ? "api/login" : "api/register",
                        new Credentials(account.Name, account.Password, account.Registered ? "" : settings.JoinCode), ct))
                    {
                        response.EnsureSuccessStatusCode();
                        session = (await response.Content.ReadFromJsonAsync<AuthSession>(ct))!;
                    }
                    saved[i] = account with { Registered = true };
                    File.WriteAllText(accountsFile, JsonSerializer.Serialize(saved, Json));
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
                    if (prepareOnly)
                    {
                        using var logout = await http.PostAsJsonAsync("api/logout", new { }, ct);
                        logout.EnsureSuccessStatusCode(); http.Dispose();
                        Console.WriteLine($"Prepared dedicated public QA account {i + 1}/{count}.");
                        continue;
                    }
                    tunnel = new GameTunnel();
                    await tunnel.Connect(settings, session.Token, ct);
                    using var launch = await http.PostAsJsonAsync("api/launch", new LaunchRequest(tunnel.GatewayPort), ct);
                    launch.EnsureSuccessStatusCode();
                    var ticket = (await launch.Content.ReadFromJsonAsync<GameLaunch>(ct))!;
                    // Launcher usernames allow underscores; the in-game roster accepts alphanumerics.
                    result.Players.Add((http, tunnel, ticket, account.Name.Replace("_", "", StringComparison.Ordinal)));
                    Console.WriteLine($"Connected public QA tunnel {i + 1}/{count}.");
                }
                catch
                {
                    if (tunnel is not null) await tunnel.DisposeAsync();
                    http.Dispose(); throw;
                }
                // Respect the service's 12/minute authentication budget with larger QA fleets.
                if (count > 8 && i + 1 < count) await Task.Delay(TimeSpan.FromSeconds(6), ct);
            }
            return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var player in Players)
        {
            try { await player.Tunnel.DisposeAsync(); } catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Net.WebSockets.WebSocketException) { }
            try
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var response = await player.Http.PostAsJsonAsync("api/logout", new { }, stop.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
            player.Http.Dispose();
        }
        Players.Clear();
    }
}
