using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Cranberry.Launcher.Core;

namespace Cranberry.Launcher;

internal sealed partial class MainForm : Form
{
    private LauncherSettings _settings;
    private HttpClient _http;
    private AuthSession? _session;
    private GameTunnel? _tunnel;
    private Process? _game;
    private GameInput? _gameInput;
    private FileStream? _gameLease;
    private CancellationTokenSource? _install;
    private bool _busy, _polling, _closing, _serverReachable;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };

    public MainForm(LauncherSettings settings)
    {
        _settings = settings; _http = LauncherConnection.CreateHttp(settings);
        BuildShell();
        _timer.Tick += async (_, _) => await Poll();
        _timer.Start();
        FormClosing += async (_, e) =>
        {
            if (_closing) return;
            if (_replacingLauncher) { e.Cancel = true; return; }
            _launcherUpdate?.Cancel();
            if (_game is { HasExited: false })
            {
                e.Cancel = true; WindowState = FormWindowState.Minimized;
                _status.Text = "Keep the launcher open while playing. Close the game before exiting.";
                return;
            }
            if (_tunnel is not null)
            {
                e.Cancel = true; _closing = true;
                await StopVoice();
                await _tunnel.DisposeAsync(); _tunnel = null;
                _install?.Cancel(); _timer.Stop(); _gameLease?.Dispose(); _http.Dispose();
                Close(); return;
            }
            _install?.Cancel(); _timer.Stop(); _gameLease?.Dispose(); _http.Dispose();
        };
        FormClosed += (_, _) => { _timer.Dispose(); _voiceTimer.Dispose(); _overlayHotkey?.Dispose(); _gameInput?.Dispose(); ClearSocial(); };
    }

    private async Task SaveSettings()
    {
        bool gameRunning = _game is { HasExited: false };
        string installDirectory = Path.GetFullPath(_folder.Text);
        bool gameSettingsChanged = _settings.ServerUrl != _server.Text || _settings.CertificateSha256 != _pin.Text
            || !StringComparer.OrdinalIgnoreCase.Equals(_settings.InstallDirectory, installDirectory) || _settings.Name != _name.Text.Trim();
        if ((gameRunning || _install is not null) && gameSettingsChanged)
            throw new InvalidOperationException("Close the game or finish the download before changing game or server settings. Voice settings can be changed now.");
        LauncherSettings.ValidateServer(_server.Text);
        if (_pin.Text.Length != 0 && (_pin.Text.Length != 64 || !_pin.Text.All(char.IsAsciiHexDigit))) throw new InvalidOperationException("Certificate fingerprint must have 64 hexadecimal characters.");
        bool changed = _settings.ServerUrl != _server.Text || _settings.CertificateSha256 != _pin.Text;
        if (!string.IsNullOrWhiteSpace(_voiceKey.Text) && Core.Voice.VoiceBindings.Parse(_voiceKey.Text).Length == 0)
            throw new InvalidOperationException("Use a supported voice key, such as KP_4, Mouse_2 or Alt+V.");
        // Registration secrets are used once and never saved to the per-user settings.
        _settings = _settings with { ServerUrl = _server.Text, InstallDirectory = installDirectory,
            CertificateSha256 = _pin.Text, Name = _name.Text.Trim(), JoinCode = "",
            ProximityVoiceEnabled = _voiceEnabled.Checked, VoiceInputDevice = (_voiceInput.SelectedItem as AudioDevice)?.Id ?? -1,
            VoiceOutputDevice = (_voiceOutput.SelectedItem as AudioDevice)?.Id ?? -1,
            VoiceVolume = (int)_voiceVolume.Value, VoicePushToTalkKey = _voiceKey.Text.Trim() };
        Program.Save(_settings);
        if (!gameRunning && _install is null) { _http.Dispose(); _http = LauncherConnection.CreateHttp(_settings); }
        if (changed) { _session = null; ClearSocial(); }
        if (_session is not null) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _session.Token);
        if (gameRunning)
        {
            await StopVoice();
            if (_game is { HasExited: false }) StartVoice(_game.Id);
        }
        _status.Text = "Settings saved.";
    }

    private async Task Authenticate(bool register)
    {
        await Action(async () =>
        {
            _accountActions!.Enabled = false;
            try
            {
                string name = _name.Text.Trim();
                if (register && (name.Length is < 3 or > 24 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')))
                    throw new InvalidOperationException("Account name must be 3-24 letters, digits or underscores.");
                if (register) PasswordPolicy.Validate(_password.Text, name);
                if (!register && (name.Length == 0 || _password.Text.Length == 0))
                    throw new InvalidOperationException("Enter your account name and password to sign in.");
                await SaveSettings();
                string code = _join.Text.Trim();
                if (register && _keepCharacter.Checked)
                    code = LocalOwnerCode(_settings) ?? throw new InvalidOperationException("The existing host character belongs to the local server. Restore its server URL and certificate, or uncheck Keep my existing host character.");
                if (register && string.IsNullOrWhiteSpace(code))
                    throw new InvalidOperationException("Enter the join code supplied with your server's launcher.");
                _accountStatus.ForeColor = Muted;
                _accountStatus.Text = _status.Text = register ? "Creating account..." : "Signing in...";
                _session = await Post<AuthSession>(register ? "api/register" : "api/login", new Credentials(name, _password.Text, register ? code : ""));
                _password.Clear();
                _serverReachable = true;
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _session.Token);
                UpdateSessionView();
                ShowPage(0);
                _accountStatus.Text = _status.Text = "Signed in. Press Play, or use Install / repair for a new game folder.";
            }
            catch (Exception ex)
            {
                _accountStatus.ForeColor = Color.Salmon;
                _accountStatus.Text = Friendly(ex);
                throw;
            }
            finally { _accountActions.Enabled = true; }
        }, false);
    }

    private static string? LocalOwnerCode(LauncherSettings settings)
    {
        string root = Program.Local?.Root ?? @"C:\Aug2017";
        try
        {
            Uri server = LauncherSettings.ValidateServer(settings.ServerUrl);
            if (server.Scheme != "https" || !server.IsLoopback) return null;
            using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "launcher-host.json")));
            if (server.Port != config.RootElement.GetProperty("Port").GetInt32()) return null;
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                Path.Combine(root, config.RootElement.GetProperty("CertificateFile").GetString()!), null);
            if (!string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), settings.CertificateSha256, StringComparison.OrdinalIgnoreCase)) return null;
            return config.RootElement.GetProperty("OwnerCode").GetString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException or InvalidOperationException or ArgumentException)
        { return null; }
    }

    private async Task Action(Func<Task> work, bool requiresLogin = true)
    {
        if (_busy) return;
        if (_startingLocalHost) { _status.Text = "Starting local server. Sign-in will be available when it is ready."; return; }
        if (_preview) { _status.Text = "Design preview. Open the launcher normally to connect."; return; }
        _busy = true; UpdateSessionView();
        try
        {
            if (requiresLogin && _session is null) throw new InvalidOperationException("Sign in first.");
            await work(); await Poll();
        }
        catch (Exception ex) { _status.Text = Friendly(ex); }
        finally { _busy = false; UpdateSessionView(); }
    }

    private async Task Poll()
    {
        if (_session is null || _polling || _closing) return;
        _polling = true;
        try
        {
            if (_game is { HasExited: true })
            {
                _overlayHotkey?.Dispose(); _overlayHotkey = null;
                await StopVoice();
                _gameInput?.Dispose(); _gameInput = null;
                _game.Dispose(); _game = null; _gameLease?.Dispose(); _gameLease = null;
                if (_tunnel is not null) { await _tunnel.DisposeAsync(); _tunnel = null; }
                _status.Text = "Game closed. Ready to play.";
            }
            var state = await Get<LauncherState>("api/state");
            _serverReachable = true;
            ApplyState(state);
            await RefreshSocialPictures(state);
            await RefreshLeaderboard();
            if (_game is { HasExited: false } && _tunnel?.Completion.IsCompleted == true)
                _status.Text = "Encrypted game connection ended. Close the game and press Play to reconnect.";
        }
        catch (Exception ex) { _serverReachable = false; _status.Text = Friendly(ex); }
        finally { _polling = false; UpdateSessionView(); }
    }

    private void ApplyState(LauncherState state)
    {
        _socialState = state;
        Fill(_friends, state.Friends.Select(p => new Item(p.AccountId, p.Name,
            (p.Online ? p.GameStatus : "Offline") + (_unread.GetValueOrDefault(p.AccountId) is > 0 and var count ? $" · {count} unread" : ""), p.Online)));
        Fill(_friendRequests, state.Invites.Select(i => new Item(i.Id, i.FromName,
            i.Kind.Equals("Friend", StringComparison.OrdinalIgnoreCase) ? "Friend request" : "Party invitation")));
        _friendsCount.Text = $"YOUR FRIENDS  ({state.Friends.Count(p => p.Online)}/{state.Friends.Count})";
        _navigation[1].Text = state.Invites.Count == 0 ? "FRIENDS" : $"FRIENDS ({state.Invites.Count})";
        _partyForm?.ApplyState(state);
        NotifyInvitations(state);
    }

    private async Task InstallOrPlay(bool play)
    {
        await Action(async () =>
        {
            if (_game is { HasExited: false }) throw new InvalidOperationException("The game is already running.");
            foreach (var game in Process.GetProcessesByName("H1Z1"))
            {
                using (game)
                    if (string.Equals(game.MainModule?.FileName, Path.Combine(_settings.InstallDirectory, "H1Z1.exe"), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Close the game before installing or launching this copy.");
            }
            _install = new CancellationTokenSource(); _cancel.Visible = true; UpdateSessionView();
            try
            {
                var manifest = await Get<GameManifest>("api/manifest");
                bool progressActive = true;
                var progress = new Progress<InstallProgress>(p =>
                {
                    if (!progressActive) return;
                    _progress.Value = p.Total == 0 ? 0 : (int)Math.Clamp(p.Complete * 100 / p.Total, 0, 100);
                    _status.Text = $"{p.Message}  •  {p.Complete / 1073741824.0:0.00} / {p.Total / 1073741824.0:0.00} GB";
                });
                try
                {
                    var downloads = await DownloadConfiguration.ReadTrustedAsync(_http, _settings, _install.Token);
                    using var installer = GameInstaller.Create(_http, _settings with { ContentBaseUrl = downloads.ContentBaseUrl });
                    await installer.Install(manifest, _settings.InstallDirectory, progress, _install.Token);
                }
                finally
                {
                    // Progress posts to the UI queue. Late callbacks must not hide completion or errors.
                    progressActive = false;
                }
                _progress.Value = 100;
                _status.Text = "Game verified. Ready to play.";
                if (!play) return;
                _gameLease = new FileStream(GameInstaller.SafePath(_settings.InstallDirectory, ".cranberry.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                _tunnel = new GameTunnel();
                await _tunnel.Connect(_settings, _session!.Token, _install.Token);
                var launch = await Post<GameLaunch>("api/launch", new LaunchRequest(_tunnel.GatewayPort, BidirectionalDoors.ProtocolVersion));
                if (launch.BuildId != manifest.BuildId) throw new InvalidOperationException("The game package changed. Run Install / repair again.");
                var info = GameProcess.StartInfo(_settings.InstallDirectory, launch with { LoginAddress = "127.0.0.1:" + _tunnel.LoginPort });
                _game = Process.Start(info) ?? throw new InvalidOperationException("Could not start the game.");
                StartLootReloadFix(_game, _settings.InstallDirectory);
                StartBinocularScopeFix(_game, _settings.InstallDirectory);
                StartOwnBulletTracerFix(_game, _settings.InstallDirectory);
                StartDoorSwingFix(_game, _settings.InstallDirectory, launch);
                _gameInput = new GameInput(_game.Id);
                StartVoice(_game.Id);
                try { StartOverlayHotkey(_game.Id); _status.Text = "Game started. Shift+Tab opens friends and messages once you enter the game."; }
                catch (System.ComponentModel.Win32Exception ex) { _status.Text = ex.Message; }
            }
            catch
            {
                if (_tunnel is not null) { await _tunnel.DisposeAsync(); _tunnel = null; }
                _gameLease?.Dispose(); _gameLease = null;
                throw;
            }
            finally { _install.Dispose(); _install = null; _cancel.Visible = false; UpdateSessionView(); }
        });
    }

    private async Task<T> Get<T>(string route)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { using var response = await _http.GetAsync(route, timeout.Token); return await Read<T>(response); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { throw ApiTimeout(); }
    }
    private async Task<T> Post<T>(string route, object body)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { using var response = await _http.PostAsJsonAsync(route, body, timeout.Token); return await Read<T>(response); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { throw ApiTimeout(); }
    }
    private static TimeoutException ApiTimeout() => new("The server did not respond within 15 seconds. Check that the Cranberry host is running, then try again.");
    private async Task Post(string route, object body) => await Post<JsonElement>(route, body);
    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests) throw new InvalidOperationException("Too many attempts. Wait a minute and retry.");
            string text = await response.Content.ReadAsStringAsync();
            try { throw new InvalidOperationException(JsonSerializer.Deserialize<ApiError>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Error ?? "Server request failed."); }
            catch (JsonException) { throw new InvalidOperationException($"Server returned {(int)response.StatusCode}."); }
        }
        return await response.Content.ReadFromJsonAsync<T>() ?? throw new InvalidDataException("Empty server response.");
    }
    private static string Friendly(Exception ex) => ex switch
    {
        OperationCanceledException => "Operation cancelled. Download progress is saved; use Install / repair to resume.",
        HttpRequestException => "Cannot connect securely. Start the Cranberry host and check the server URL and certificate fingerprint.",
        _ => ex.Message,
    };
    internal sealed record Item(string Id, string Text, string Detail = "", bool Online = false) { public override string ToString() => Text; }
    private static string Selected(ListBox list) => (list.SelectedItem as Item)?.Id ?? throw new InvalidOperationException("Select a friend or request first.");
    private static void Fill(ListBox list, IEnumerable<Item> items)
    {
        string? selected = (list.SelectedItem as Item)?.Id;
        list.BeginUpdate(); list.Items.Clear();
        foreach (var item in items) { int index = list.Items.Add(item); if (item.Id == selected) list.SelectedIndex = index; }
        list.EndUpdate();
    }
    internal void RenderPreview(string page = "game")
    {
        _preview = true; _timer.Stop();
        if (page is not ("account" or "register" or "error" or "signed-out"))
        {
            _session = new AuthSession("preview-only", "owner", "Samuel");
            _serverReachable = true;
            ApplyState(new(new("owner", "Samuel", true),
                [new("friend", "Alex", true, "In main menu"), new("other", "Jamie", false)],
                [new("request", "casey", "Casey", "Friend")], null));
            _status.Text = "Ready to play.";
        }
        _name.Text = ""; _join.Text = ""; _keepCharacter.Visible = false;
        _settings = _settings with { ServerUrl = "https://server.example/" };
        _server.Text = _settings.ServerUrl;
        _folder.Text = @"C:\Games\H1Z1"; _pin.Text = "";
        if (page == "empty") ApplyState(new(new("owner", "Samuel", true), [], [], null));
        ShowPage(page is "friends" or "empty" ? 1 : page is "account" or "profile" or "register" or "error" ? 2 : page == "settings" ? 3 : page == "leaderboard" ? 4 : 0);
        if (page == "leaderboard") PreviewLeaderboard();
        if (page == "profile") ShowProfilePicture(string.Concat(Enumerable.Range(0, 48 * 48).Select(i =>
            i / 48 < 24 ? "598696" : i % 48 < i / 48 ? "326856" : "B4C3A2")));
        if (page == "register") SetRegistration(true);
        if (page == "error") { _accountStatus.ForeColor = Color.Salmon; _accountStatus.Text = "Account name or password is incorrect. Try again."; }
        if (page == "download") { _progress.Value = 42; _cancel.Visible = true; _status.Text = "Downloading game files.  5.72 / 13.58 GB"; }
        UpdateSessionView();
        if (page == "download") { _play.Text = "UPDATING"; _play.Enabled = _repair.Enabled = false; }
    }
}
