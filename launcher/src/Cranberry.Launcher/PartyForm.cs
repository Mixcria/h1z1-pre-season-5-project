using Cranberry.Launcher.Core;

namespace Cranberry.Launcher;

/// <summary>Party controls remain accessible even when the game's overlay cannot open.</summary>
internal sealed class PartyForm : Form
{
    private readonly Label _members = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Label _hint = new() { Dock = DockStyle.Fill, Text = "Invite an online friend from the launcher Friends page.\nEvery member must reach the game menu and press Ready." };
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly LauncherButton _ready = new("READY") { Width = 98 };
    private readonly LauncherButton _queue = new("JOIN QUEUE") { Width = 112 };
    private readonly LauncherButton _leave = new("LEAVE PARTY") { Width = 120, Quiet = true };
    private LauncherState? _state;

    public PartyForm(Func<string, object, Task> send, Func<Task> overlay)
    {
        Text = "My party - Cranberry"; Icon = LauncherTheme.Icon();
        ClientSize = new(500, 310); MinimumSize = Size;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = LauncherTheme.Background; ForeColor = LauncherTheme.Text; Font = LauncherTheme.Body();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(20), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new(SizeType.Absolute, 44)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 52)); layout.RowStyles.Add(new(SizeType.Absolute, 40));
        _mode.Items.AddRange(["Solo", "Duos", "Fives"]); _mode.SelectedIndex = 1;
        var modeRow = new FlowLayoutPanel { Dock = DockStyle.Fill };
        modeRow.Controls.Add(new Label { Text = "Game mode", AutoSize = true, Margin = new(0, 5, 12, 0) }); modeRow.Controls.Add(_mode);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var openOverlay = new LauncherButton("OVERLAY") { Width = 90, Quiet = true };
        openOverlay.Click += async (_, _) => await overlay();
        actions.Controls.AddRange([_ready, _queue, _leave, openOverlay]);
        layout.Controls.Add(modeRow, 0, 0); layout.Controls.Add(_members, 0, 1);
        layout.Controls.Add(_hint, 0, 2); layout.Controls.Add(actions, 0, 3); Controls.Add(layout);
        _mode.SelectionChangeCommitted += async (_, _) => await send("api/party/mode", new ModeRequest((string)_mode.SelectedItem!));
        _ready.Click += async (_, _) => await send("api/party/ready", new ReadyRequest(!IsReady));
        _queue.Click += async (_, _) => await send("api/party/queue", new { });
        _leave.Click += async (_, _) => await send("api/party/leave", new { });
    }

    private bool IsReady => _state?.Lobby?.Members.Any(m => m.AccountId == _state.Me.AccountId && m.Ready) == true;
    public void ShowStatus(string message) => _hint.Text = message;

    public void ApplyState(LauncherState state)
    {
        _state = state;
        var lobby = state.Lobby;
        bool leader = lobby is null || lobby.LeaderId == state.Me.AccountId;
        _mode.Enabled = leader && lobby?.InGame != true;
        _mode.SelectedItem = lobby?.Mode ?? "Duos";
        _members.Text = lobby is null ? state.Me.Name + "  -  Not ready" : string.Join("\n", lobby.Members.Select(m =>
            m.Name + (m.AccountId == lobby.LeaderId ? " (leader)" : "") + "  -  "
            + (m.Ready ? "Ready" : "Not ready") + "  /  " + (m.Online ? m.GameStatus : "Offline")));
        _ready.Text = IsReady ? "UNREADY" : "READY";
        _ready.Enabled = lobby?.InGame != true;
        _queue.Enabled = leader && lobby is not null && !lobby.InGame && lobby.Members.All(m => m.Ready && m.Online && m.GameStatus == "Menu");
        if (lobby?.InGame == true) _hint.Text = "You are in the same game lobby. The leader can select Duos or Fives and queue from the game menu.";
        _leave.Enabled = lobby is not null;
    }
}
