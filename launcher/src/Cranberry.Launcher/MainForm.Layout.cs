using System.Runtime.InteropServices;
using Cranberry.Launcher.Core;

namespace Cranberry.Launcher;

internal sealed partial class MainForm
{
    private static readonly Color Muted = LauncherTheme.Muted;
    private readonly Label _status = TextLabel("Sign in to play.", 9);
    private readonly Label _launchTitle = TextLabel("H1Z1: KING OF THE KILL", 14, heading: true);
    private readonly LauncherButton _identity = new("SIGN IN") { Quiet = true, Width = 180 };
    private readonly Panel _pages = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly List<Panel> _pageViews = [];
    private readonly List<LauncherButton> _navigation = [];
    private readonly LauncherProgress _progress = new() { Dock = DockStyle.Top };
    private readonly LauncherList _friends = new() { EmptyText = "Add a friend by their account name." },
        _friendRequests = new() { EmptyText = "No pending requests or invitations." };
    private readonly TextBox _friendName = Input("Account name"), _server = Input(), _folder = Input(),
        _pin = Input(), _name = Input("Account name"), _password = Input("Password"), _join = Input("Server join code");
    private readonly LauncherButton _play = new("PLAY") { Primary = true },
        _repair = new("INSTALL / REPAIR") { Quiet = true, Width = 140, Height = 26 },
        _cancel = new("CANCEL DOWNLOAD") { Quiet = true, Width = 152, Height = 26, Visible = false };
    private readonly CheckBox _keepCharacter = new() { Text = "Keep my existing host character", AutoSize = true, Margin = new(0, 8, 0, 6) };
    private readonly Label _accountStatus = TextLabel("", 9);
    private FlowLayoutPanel? _accountActions;
    private readonly FlowLayoutPanel _registration = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
    private readonly LauncherButton _authenticate = new("SIGN IN") { Primary = true, Width = 154 };
    private readonly LauncherButton _registerToggle = new("CREATE ACCOUNT") { Quiet = true, Width = 162 };
    private readonly LauncherButton _logout = new("SIGN OUT") { Width = 130 };
    private readonly Label _accountHeading = TextLabel("SIGN IN", 25, heading: true);
    private readonly Label _accountHint = TextLabel("Use your Cranberry account to play.");
    private readonly Label _friendsCount = TextLabel("YOUR FRIENDS", 14, heading: true);
    private readonly Label _friendsHint = TextLabel("Add friends by their Cranberry account name.");
    private readonly TableLayoutPanel _friendsBody = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
    private readonly Label _connection = TextLabel("NOT SIGNED IN", 8);
    private bool _registering, _preview;
    private int _pageIndex;

    private static TextBox Input(string placeholder = "") => new() { PlaceholderText = placeholder,
        Width = 340, BackColor = LauncherTheme.Field, ForeColor = LauncherTheme.Text,
        BorderStyle = BorderStyle.FixedSingle, Font = LauncherTheme.Body(11), Margin = new(0, 0, 0, 12) };
    private static Label TextLabel(string text, float size = 9.5f, bool heading = false) => new()
    {
        Text = text, AutoSize = false, Height = heading ? (int)(size * 2 + 8) : 24,
        ForeColor = heading ? LauncherTheme.Text : Muted, BackColor = Color.Transparent,
        Font = heading ? LauncherTheme.Heading(size) : LauncherTheme.Body(size),
        TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty,
        AutoEllipsis = true, UseMnemonic = false
    };
    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new(0, 0, 0, 8), Padding = Padding.Empty };
        row.Controls.AddRange(controls); return row;
    }
    private static void Field(FlowLayoutPanel flow, string title, TextBox input, int width = 340)
    {
        var label = TextLabel(title, 9); label.Width = width; label.Height = 25;
        input.Width = width; input.AccessibleName = title;
        flow.Controls.Add(label); flow.Controls.Add(input);
    }

    private void BuildShell()
    {
        SuspendLayout();
        Text = "Cranberry - H1Z1: King of the Kill"; Icon = LauncherTheme.Icon();
        ClientSize = new(1000, 660); MinimumSize = new(920, 640);
        StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.None;
        BackColor = LauncherTheme.Background; ForeColor = LauncherTheme.Text;
        Font = LauncherTheme.Body(); AutoScaleDimensions = new(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true; Padding = new(1); KeyPreview = true;

        var title = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = Color.FromArgb(15, 15, 15) };
        var titleText = TextLabel("H1Z1  /  AUGUST 2017", 8); titleText.Dock = DockStyle.Fill; titleText.Padding = new(17, 0, 0, 0);
        title.Controls.Add(titleText); DragHandle(title); DragHandle(titleText);
        foreach (var (caption, action) in new (string, System.Action)[] {
            ("\u2013", () => WindowState = FormWindowState.Minimized), ("\u25a1", ToggleMaximize), ("\u00d7", Close) })
        {
            var control = new LauncherButton(caption) { Dock = DockStyle.Right, Width = 43, Quiet = true, Font = LauncherTheme.Body(14), Margin = Padding.Empty };
            control.AccessibleName = caption == "\u00d7" ? "Close launcher" : caption == "\u25a1" ? "Maximize launcher" : "Minimize launcher";
            control.Click += (_, _) => action(); title.Controls.Add(control);
        }
        var navigation = new TableLayoutPanel { Dock = DockStyle.Top, Height = 59, ColumnCount = 3, RowCount = 1, Padding = new(24, 0, 16, 0), Margin = Padding.Empty };
        navigation.RowStyles.Add(new(SizeType.Percent, 100));
        navigation.ColumnStyles.Add(new(SizeType.Absolute, 155));
        navigation.ColumnStyles.Add(new(SizeType.Absolute, 500));
        navigation.ColumnStyles.Add(new(SizeType.Percent, 100));
        var brand = TextLabel("CRANBERRY", 19, heading: true); brand.Dock = DockStyle.Fill; navigation.Controls.Add(brand, 0, 0);
        var links = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        string[] names = ["GAME", "FRIENDS", "ACCOUNT", "SETTINGS", "LEADERBOARD"];
        for (int i = 0; i < names.Length; i++)
        {
            int page = i;
            var button = new LauncherButton(names[i]) { Navigation = true, Width = i == 4 ? 138 : 88, Height = 59, Margin = Padding.Empty, Font = LauncherTheme.Heading(12) };
            button.Click += (_, _) => ShowPage(page); _navigation.Add(button); links.Controls.Add(button);
            var view = new Panel { Dock = DockStyle.Fill, Visible = false, Margin = Padding.Empty };
            _pageViews.Add(view); _pages.Controls.Add(view);
        }
        navigation.Controls.Add(links, 1, 0); _identity.Dock = DockStyle.Fill; _identity.Margin = new(12, 10, 0, 10);
        _identity.Click += (_, _) => ShowPage(2); navigation.Controls.Add(_identity, 2, 0);
        navigation.Paint += (_, e) => { using var pen = new Pen(LauncherTheme.Border); e.Graphics.DrawLine(pen, 0, navigation.Height - 1, navigation.Width, navigation.Height - 1); };

        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 26, ColumnCount = 2, Padding = new(28, 0, 28, 0), BackColor = Color.FromArgb(17, 17, 17) };
        footer.ColumnStyles.Add(new(SizeType.Percent, 50)); footer.ColumnStyles.Add(new(SizeType.Percent, 50));
        _connection.Dock = DockStyle.Fill; footer.Controls.Add(_connection, 0, 0);
        var version = TextLabel("CLIENT 0.0.118.208059", 8); version.TextAlign = ContentAlignment.MiddleRight; version.Dock = DockStyle.Fill; footer.Controls.Add(version, 1, 0);
        var launch = new Panel { Dock = DockStyle.Bottom, Height = 116 };
        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new(28, 13, 28, 9) };
        bar.ColumnStyles.Add(new(SizeType.Percent, 100)); bar.ColumnStyles.Add(new(SizeType.Absolute, 210));
        bar.RowStyles.Add(new(SizeType.Absolute, 28)); bar.RowStyles.Add(new(SizeType.Percent, 100)); bar.RowStyles.Add(new(SizeType.Absolute, 26));
        _launchTitle.Dock = DockStyle.Fill; bar.Controls.Add(_launchTitle, 0, 0);
        _status.Dock = DockStyle.Fill; _status.Padding = new(0, 0, 16, 0); bar.Controls.Add(_status, 0, 1);
        var utilities = Row(_repair, _cancel); utilities.Dock = DockStyle.Fill; utilities.Margin = Padding.Empty; bar.Controls.Add(utilities, 0, 2);
        _play.Dock = DockStyle.Fill; _play.Margin = new(0, 4, 0, 0); _play.Font = LauncherTheme.Heading(23);
        bar.Controls.Add(_play, 1, 0); bar.SetRowSpan(_play, 2);
        var build = TextLabel("EARLY AUGUST 2017", 8); build.TextAlign = ContentAlignment.MiddleCenter; build.Dock = DockStyle.Fill; bar.Controls.Add(build, 1, 2);
        launch.Controls.Add(bar); launch.Controls.Add(_progress);

        Controls.Add(_pages); Controls.Add(launch); Controls.Add(footer); Controls.Add(navigation); Controls.Add(title);
        _pageViews[0].Controls.Add(new ArtworkPanel { Dock = DockStyle.Fill });
        BuildFriends(_pageViews[1]); BuildAccount(_pageViews[2]); BuildSettings(_pageViews[3]);
        BuildLeaderboard(_pageViews[4]);
        _play.Click += async (_, _) => { if (_session is null) { ShowPage(2); _name.Focus(); } else await InstallOrPlay(true); };
        _repair.Click += async (_, _) => { if (_session is null) { ShowPage(2); _name.Focus(); } else await InstallOrPlay(false); };
        _cancel.Click += (_, _) => { _install?.Cancel(); _launcherUpdate?.Cancel(); };
        ShowPage(0); UpdateSessionView(); ResumeLayout(true);
    }

    private void BuildFriends(Panel page)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new(38, 20, 38, 20) };
        layout.RowStyles.Add(new(SizeType.Absolute, 49)); layout.RowStyles.Add(new(SizeType.Absolute, 39)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        page.Controls.Add(layout);
        var heading = TextLabel("FRIENDS", 25, heading: true); heading.Dock = DockStyle.Fill; layout.Controls.Add(heading, 0, 0);
        _friendsHint.Dock = DockStyle.Fill; layout.Controls.Add(_friendsHint, 0, 1);
        _friendsBody.ColumnStyles.Add(new(SizeType.Percent, 55)); _friendsBody.ColumnStyles.Add(new(SizeType.Percent, 45));
        _friendsBody.RowStyles.Add(new(SizeType.Percent, 100)); layout.Controls.Add(_friendsBody, 0, 2);

        var social = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = new(0, 0, 28, 0) };
        foreach (var style in new RowStyle[] { new(SizeType.Absolute, 34), new(SizeType.Percent, 100), new(SizeType.Absolute, 28), new(SizeType.Absolute, 37), new(SizeType.Absolute, 36) }) social.RowStyles.Add(style);
        _friendsCount.Dock = DockStyle.Fill; social.Controls.Add(_friendsCount, 0, 0); social.Controls.Add(_friends, 0, 1);
        var friendLabel = TextLabel("Add a friend", 9); friendLabel.Dock = DockStyle.Fill; social.Controls.Add(friendLabel, 0, 2);
        var add = new LauncherButton("ADD FRIEND") { Width = 112, Height = 29, Margin = Padding.Empty };
        var addRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        addRow.ColumnStyles.Add(new(SizeType.Percent, 100)); addRow.ColumnStyles.Add(new(SizeType.Absolute, 112));
        _friendName.Dock = DockStyle.Fill; _friendName.Margin = new(0, 0, 7, 0); _friendName.AccessibleName = "Friend's account name";
        addRow.Controls.Add(_friendName, 0, 0); addRow.Controls.Add(add, 1, 0); social.Controls.Add(addRow, 0, 3);
        var remove = new LauncherButton("REMOVE") { Quiet = true, Width = 80 };
        var message = new LauncherButton("MESSAGE") { Width = 84 };
        var invite = new LauncherButton("INVITE TO PARTY") { Width = 128 };
        social.Controls.Add(Row(message, invite, remove), 0, 4);
        invite.Click += async (_, _) => await Action(async () =>
        {
            await Post("api/party/invite", new TargetRequest(Selected(_friends)));
            _status.Text = "Party invitation sent. Your friend can accept it in Friends.";
            OpenParty();
        });
        message.Click += (_, _) => OpenConversation();
        _friends.DoubleClick += (_, _) => OpenConversation();
        _friends.AvatarProvider = id => _avatars.GetValueOrDefault(id).Picture;
        _friendsBody.Controls.Add(social, 0, 0);

        var requests = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        requests.RowStyles.Add(new(SizeType.Absolute, 34)); requests.RowStyles.Add(new(SizeType.Percent, 100)); requests.RowStyles.Add(new(SizeType.Absolute, 44));
        var requestsHeading = TextLabel("REQUESTS & INVITATIONS", 14, heading: true); requestsHeading.Dock = DockStyle.Fill;
        requests.Controls.Add(requestsHeading, 0, 0); requests.Controls.Add(_friendRequests, 0, 1);
        var accept = new LauncherButton("ACCEPT") { Width = 86 }; var decline = new LauncherButton("DECLINE") { Quiet = true, Width = 86 };
        var party = new LauncherButton("MY PARTY") { Width = 94 }; party.Click += (_, _) => OpenParty();
        var responses = Row(accept, decline, party); responses.Margin = new(0, 8, 0, 0); requests.Controls.Add(responses, 0, 2); _friendsBody.Controls.Add(requests, 1, 0);
        add.Click += async (_, _) => await Action(async () => { await Post("api/friends", new TargetRequest(_friendName.Text.Trim())); _friendName.Clear(); _status.Text = "Friend request sent."; });
        remove.Click += async (_, _) => await Action(() => Post("api/friends/remove", new TargetRequest(Selected(_friends))));
        accept.Click += async (_, _) => await Action(async () =>
        {
            string id = Selected(_friendRequests);
            bool isParty = _socialState?.Invites.Any(i => i.Id == id && i.Kind != "Friend") == true;
            await Post("api/invites/respond", new RespondRequest(id, true));
            if (isParty) { _status.Text = "Party joined. Launch the game, then ready up in My party."; OpenParty(); }
        });
        decline.Click += async (_, _) => await Action(() => Post("api/invites/respond", new RespondRequest(Selected(_friendRequests), false)));
        _friends.SelectedIndexChanged += (_, _) => { remove.Enabled = _friends.SelectedItem is Item; invite.Enabled = _friends.SelectedItem is Item { Online: true }; };
        _friendRequests.SelectedIndexChanged += (_, _) => accept.Enabled = decline.Enabled = _friendRequests.SelectedItem is Item;
        remove.Enabled = invite.Enabled = accept.Enabled = decline.Enabled = false;
    }

    private void BuildAccount(Panel page)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new(SizeType.Absolute, 450)); grid.ColumnStyles.Add(new(SizeType.Percent, 100)); page.Controls.Add(grid);
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new(38, 20, 24, 20), Margin = Padding.Empty };
        grid.Controls.Add(flow, 0, 0); grid.Controls.Add(new ArtworkPanel { Dock = DockStyle.Fill, SideCrop = true, Margin = Padding.Empty }, 1, 0);
        _accountHeading.Width = _accountHint.Width = 360; _accountHeading.Height = 49;
        _accountHint.Margin = new(0, 0, 0, 14); flow.Controls.Add(_accountHeading); flow.Controls.Add(_accountHint);
        BuildProfileControls(flow);
        var changePassword = new LauncherButton("CHANGE PASSWORD") { Width = 180, Margin = new(0, 8, 0, 12) };
        _profileControls.Controls.Add(changePassword);
        changePassword.Click += (_, _) =>
        {
            if (_session is null) return;
            using var dialog = new ChangePasswordForm(_session.Name, async request =>
            {
                await Post("api/account/password", request);
                _accountStatus.ForeColor = LauncherTheme.Online;
                _accountStatus.Text = _status.Text = "Password changed. Other launcher sign-ins have expired.";
            });
            dialog.ShowDialog(this);
        };
        _name.Text = _settings.Name; _password.UseSystemPasswordChar = true; _join.UseSystemPasswordChar = true; _join.Text = _settings.JoinCode;
        Field(_signInFields, "Account name", _name); Field(_signInFields, "Password", _password); flow.Controls.Add(_signInFields);
        Field(_registration, "Join code", _join);
        var passwordHint = TextLabel(PasswordPolicy.Guidance, 9);
        passwordHint.Width = 340; passwordHint.Height = 60; _registration.Controls.Add(passwordHint);
        _keepCharacter.Visible = LocalOwnerCode(_settings) is not null; _keepCharacter.Checked = _keepCharacter.Visible;
        _join.Enabled = !_keepCharacter.Checked; _keepCharacter.CheckedChanged += (_, _) => _join.Enabled = !_keepCharacter.Checked;
        _registration.Controls.Add(_keepCharacter); _registration.Visible = false; flow.Controls.Add(_registration);
        _accountActions = Row(_authenticate, _registerToggle); flow.Controls.Add(_accountActions);
        _accountStatus.Width = 350; _accountStatus.Height = 52; _accountStatus.Margin = new(0, 6, 0, 0); flow.Controls.Add(_accountStatus);
        _logout.Visible = false; flow.Controls.Add(_logout);
        _registerToggle.Click += (_, _) => SetRegistration(!_registering);
        _authenticate.Click += async (_, _) => await Authenticate(_registering);
        _password.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await Authenticate(_registering); } };
        _logout.Click += async (_, _) => await Action(async () =>
        {
            if (_game is { HasExited: false }) throw new InvalidOperationException("Close the game before signing out.");
            await Post("api/logout", new { }); _session = null; _http.DefaultRequestHeaders.Authorization = null;
            ClearSocial();
            _friends.Items.Clear(); _friendRequests.Items.Clear();
            _accountStatus.Text = "Signed out."; _status.Text = "Sign in to play."; UpdateSessionView();
        });
    }

    private void BuildSettings(Panel page)
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new(38, 20, 38, 20) };
        page.Controls.Add(flow);
        var heading = TextLabel("SETTINGS", 25, heading: true); heading.Width = 500; flow.Controls.Add(heading);
        var hint = TextLabel("Your game folder and server connection."); hint.Width = 700; hint.Margin = new(0, 0, 0, 12); flow.Controls.Add(hint);
        _server.Text = _settings.ServerUrl; _folder.Text = _settings.InstallDirectory; _pin.Text = _settings.CertificateSha256;
        Field(flow, "Game folder", _folder, 660);
        var browse = new LauncherButton("BROWSE...") { Width = 119 }; var open = new LauncherButton("OPEN FOLDER") { Quiet = true, Width = 130 };
        flow.Controls.Add(Row(browse, open));
        Field(flow, "Server URL", _server, 660);
        var advanced = new LauncherButton("SERVER CERTIFICATE  +") { Quiet = true, Width = 207, Height = 30 };
        flow.Controls.Add(advanced);
        var certificate = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Visible = false, Margin = Padding.Empty };
        Field(certificate, "Certificate fingerprint (SHA-256)", _pin, 660);
        var certificateHint = TextLabel("Supplied with your server's launcher. Only change it when the server owner provides a new fingerprint.", 9);
        certificateHint.Width = 700; certificateHint.Height = 36; certificate.Controls.Add(certificateHint); flow.Controls.Add(certificate);
        advanced.Click += (_, _) => { certificate.Visible = !certificate.Visible; advanced.Text = certificate.Visible ? "SERVER CERTIFICATE  -" : "SERVER CERTIFICATE  +"; };
        BuildVoiceSettings(flow);
        var save = new LauncherButton("SAVE SETTINGS") { Width = 146 }; flow.Controls.Add(Row(save));
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { SelectedPath = _folder.Text }; if (dialog.ShowDialog(this) == DialogResult.OK) _folder.Text = dialog.SelectedPath; };
        open.Click += (_, _) => { if (Directory.Exists(_folder.Text)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_folder.Text) { UseShellExecute = true }); else _status.Text = "Choose an existing game folder first."; };
        save.Click += async (_, _) => await Action(async () => { await SaveSettings(); UpdateSessionView(); }, false);
    }

    private void SetRegistration(bool value)
    {
        _registering = value; _registration.Visible = value; _accountHeading.Text = value ? "CREATE ACCOUNT" : "SIGN IN";
        _accountHint.Text = value ? "Use 3-24 letters, digits or underscores for your name." : "Use your Cranberry account to play.";
        _password.PlaceholderText = value ? "15-128 characters or a unique passphrase" : "Password";
        _authenticate.Text = value ? "CREATE ACCOUNT" : "SIGN IN"; _registerToggle.Text = value ? "BACK TO SIGN IN" : "CREATE ACCOUNT";
        _accountStatus.Text = "";
    }

    private void ShowPage(int index)
    {
        _pageIndex = index;
        for (int i = 0; i < _pageViews.Count; i++)
        {
            _pageViews[i].Visible = i == index; _navigation[i].Selected = i == index; _navigation[i].Invalidate();
        }
        _pageViews[index].BringToFront();
        if (index == 4) _ = RefreshLeaderboard(true);
    }

    private void UpdateSessionView()
    {
        bool signedIn = _session is not null;
        _leaderboardRefresh.Enabled = signedIn && !_leaderboardLoading;
        if (!signedIn)
        {
            _leaderboardPlayers.Rows.Clear(); ShowLeaderboardPlayer(null);
            _leaderboardMe.Text = "Sign in to view the standings."; _leaderboardStatus.Text = "Sign in to view Solo, Duos and Fives leaderboards.";
        }
        _profileControls.Visible = signedIn;
        _profileControls.Enabled = signedIn && !_busy;
        _signInFields.Visible = !signedIn;
        if (_accountActions is not null) _accountActions.Visible = !signedIn;
        _identity.Text = signedIn ? _session!.Name : "SIGN IN";
        _identity.AccessibleName = signedIn ? "Account: " + _session!.Name : "Sign in to your account";
        _connection.Text = signedIn ? (_serverReachable
            ? new Uri(_settings.ServerUrl).IsLoopback ? "CONNECTED TO LOCAL SERVER" : "CONNECTED TO LIVE SERVERS"
            : "CONNECTION INTERRUPTED") : _startingLocalHost ? "STARTING LOCAL SERVER" : "NOT SIGNED IN";
        _connection.ForeColor = signedIn && _serverReachable ? LauncherTheme.Online : Muted;
        _logout.Visible = signedIn; _authenticate.Enabled = _registerToggle.Enabled = !signedIn && !_startingLocalHost && !_busy;
        _name.Enabled = _password.Enabled = !signedIn;
        _registration.Visible = _registering && !signedIn;
        if (signedIn) { _accountHeading.Text = "YOUR ACCOUNT"; _accountHint.Text = "Signed in as " + _session!.Name + "."; }
        else { _accountHeading.Text = _registering ? "CREATE ACCOUNT" : "SIGN IN"; _accountHint.Text = _registering ? "Use 3-24 letters, digits or underscores for your name." : "Use your Cranberry account to play."; }
        _friendsHint.Text = signedIn ? "Add friends by their Cranberry account name." : "Sign in to manage your friends.";
        _friendsBody.Enabled = signedIn;
        bool running = _game is { HasExited: false };
        _play.Enabled = _repair.Enabled = !_busy && !_startingLocalHost && _install is null && !running;
        _play.Text = running ? "RUNNING" : _install is not null ? "UPDATING" : "PLAY";
        _play.Invalidate();
    }

    private void ToggleMaximize()
    {
        if (WindowState == FormWindowState.Normal) MaximizedBounds = Screen.FromControl(this).WorkingArea;
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
    }
    private void DragHandle(Control control)
    {
        control.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); } };
        control.DoubleClick += (_, _) => ToggleMaximize();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); using var pen = new Pen(LauncherTheme.Border); e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != 0x84 || WindowState != FormWindowState.Normal) return;
        Point point = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
        int edge = (int)(5 * DeviceDpi / 96f);
        bool left = point.X < edge, right = point.X >= Width - edge, top = point.Y < edge, bottom = point.Y >= Height - edge;
        int hit = top && left ? 13 : top && right ? 14 : bottom && left ? 16 : bottom && right ? 17 : left ? 10 : right ? 11 : top ? 12 : bottom ? 15 : 0;
        if (hit != 0) m.Result = new IntPtr(hit);
    }
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
