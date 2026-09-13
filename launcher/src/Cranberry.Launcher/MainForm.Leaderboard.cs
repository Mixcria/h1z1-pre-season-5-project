using Cranberry.Launcher.Core;

namespace Cranberry.Launcher;

internal sealed partial class MainForm
{
    private readonly DataGridView _leaderboardPlayers = ScoreGrid(), _bestGames = ScoreGrid();
    private readonly Label _leaderboardStatus = TextLabel("Sign in to view the leaderboard.", 8.5f);
    private readonly Label _leaderboardMe = TextLabel("Your standing appears here.", 9);
    private readonly Label _leaderboardPlayer = TextLabel("SELECT A PLAYER", 17, heading: true);
    private readonly Label _leaderboardStats = TextLabel("See their ten highest scores.", 9);
    private readonly List<LauncherButton> _leaderboardModes = [];
    private readonly LauncherButton _leaderboardRefresh = new("REFRESH") { Quiet = true, Width = 84, Height = 30 };
    private string _leaderboardMode = "Solo";
    private bool _leaderboardLoading;
    private DateTimeOffset _leaderboardNextRefresh;

    private static DataGridView ScoreGrid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoGenerateColumns = false,
        BackgroundColor = LauncherTheme.Background, BorderStyle = BorderStyle.None,
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, GridColor = LauncherTheme.Border,
        EnableHeadersVisualStyles = false, ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
        ColumnHeadersHeight = 27, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        DefaultCellStyle = new() { BackColor = LauncherTheme.Background, ForeColor = LauncherTheme.Text,
            SelectionBackColor = Color.FromArgb(64, 37, 37), SelectionForeColor = Color.White, Font = LauncherTheme.Body(9), Padding = new(4, 0, 4, 0) },
        ColumnHeadersDefaultCellStyle = new() { BackColor = LauncherTheme.Surface, ForeColor = Muted,
            Font = LauncherTheme.Body(8), SelectionBackColor = LauncherTheme.Surface, Padding = new(4, 0, 4, 0) },
        RowTemplate = { Height = 29 }, Margin = Padding.Empty
    };

    private static void ScoreColumn(DataGridView grid, string title, int width = 0)
    {
        var column = new DataGridViewTextBoxColumn { HeaderText = title, SortMode = DataGridViewColumnSortMode.NotSortable,
            AutoSizeMode = width == 0 ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
            MinimumWidth = width == 0 ? 90 : width };
        if (width > 0) column.Width = width;
        grid.Columns.Add(column);
    }

    private void BuildLeaderboard(Panel page)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new(28, 10, 28, 8), Margin = Padding.Empty };
        layout.RowStyles.Add(new(SizeType.Absolute, 40)); layout.RowStyles.Add(new(SizeType.Absolute, 34));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 25));
        page.Controls.Add(layout);
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        heading.ColumnStyles.Add(new(SizeType.Absolute, 215)); heading.ColumnStyles.Add(new(SizeType.Percent, 100));
        var title = TextLabel("LEADERBOARD", 24, heading: true); title.Dock = DockStyle.Fill; heading.Controls.Add(title, 0, 0);
        _leaderboardMe.Dock = DockStyle.Fill; _leaderboardMe.TextAlign = ContentAlignment.MiddleRight; heading.Controls.Add(_leaderboardMe, 1, 0);
        layout.Controls.Add(heading, 0, 0);
        var modes = Row(); modes.Dock = DockStyle.Fill;
        foreach (string mode in new[] { "Solo", "Duos", "Fives" })
        {
            var button = new LauncherButton(mode.ToUpperInvariant()) { Navigation = true, Selected = mode == _leaderboardMode, Width = 88, Height = 30, Margin = new(0, 0, 6, 0) };
            button.Click += async (_, _) =>
            {
                if (_leaderboardMode == mode) return;
                _leaderboardMode = mode;
                foreach (var choice in _leaderboardModes) { choice.Selected = choice.Text == mode.ToUpperInvariant(); choice.Invalidate(); }
                _leaderboardPlayers.Rows.Clear(); ShowLeaderboardPlayer(null); _leaderboardMe.Text = "";
                if (_preview) PreviewLeaderboard(); else await RefreshLeaderboard(true);
            };
            _leaderboardModes.Add(button); modes.Controls.Add(button);
        }
        _leaderboardRefresh.Click += async (_, _) => await RefreshLeaderboard(true);
        modes.Controls.Add(_leaderboardRefresh); layout.Controls.Add(modes, 0, 1);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        body.ColumnStyles.Add(new(SizeType.Percent, 61)); body.ColumnStyles.Add(new(SizeType.Percent, 39));
        body.RowStyles.Add(new(SizeType.Percent, 100)); layout.Controls.Add(body, 0, 2);
        ScoreColumn(_leaderboardPlayers, "#", 40); ScoreColumn(_leaderboardPlayers, "PLAYER");
        ScoreColumn(_leaderboardPlayers, "BEST 10 TOTAL", 111); ScoreColumn(_leaderboardPlayers, "K/D", 61);
        _leaderboardPlayers.AccessibleName = "Players ranked by their best ten total scores";
        _leaderboardPlayers.Margin = new(0, 0, 20, 0); body.Controls.Add(_leaderboardPlayers, 0, 0);
        _leaderboardPlayers.SelectionChanged += (_, _) => ShowLeaderboardPlayer(_leaderboardPlayers.SelectedRows.Count > 0
            ? _leaderboardPlayers.SelectedRows[0].Tag as LeaderboardEntry : null);
        var detail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        detail.RowStyles.Add(new(SizeType.Absolute, 30)); detail.RowStyles.Add(new(SizeType.Absolute, 40)); detail.RowStyles.Add(new(SizeType.Percent, 100));
        _leaderboardPlayer.Dock = DockStyle.Fill; _leaderboardStats.Dock = DockStyle.Fill;
        detail.Controls.Add(_leaderboardPlayer, 0, 0); detail.Controls.Add(_leaderboardStats, 0, 1);
        ScoreColumn(_bestGames, "#", 32); ScoreColumn(_bestGames, "SCORE"); ScoreColumn(_bestGames, "KILLS", 48); ScoreColumn(_bestGames, "PLACE", 57);
        _bestGames.RowTemplate.Height = 19; _bestGames.ColumnHeadersHeight = 21;
        _bestGames.AccessibleName = "Selected player's top ten match results";
        detail.Controls.Add(_bestGames, 0, 2); body.Controls.Add(detail, 1, 0);
        _leaderboardStatus.Dock = DockStyle.Fill; layout.Controls.Add(_leaderboardStatus, 0, 3);
    }

    private static string KillDeath(LeaderboardEntry player) => player.KdMatches == 0 ? "—"
        : player.KdDeaths == 0 ? (player.KdKills == 0 ? "0.00" : "∞")
        : ((double)player.KdKills / player.KdDeaths).ToString("0.00");

    private void ShowLeaderboardPlayer(LeaderboardEntry? player)
    {
        _bestGames.Rows.Clear();
        _leaderboardPlayer.Text = player?.Name ?? "SELECT A PLAYER";
        _leaderboardStats.Text = player is null ? "See their ten highest scores."
            : $"{player.Tier}  ·  {player.Matches:N0} matches  ·  {player.Wins:N0} wins\n{player.Kills:N0} kills  ·  K/D {KillDeath(player)}  ·  {player.Best.Count}/10 scores";
        if (player is null) return;
        for (int index = 0; index < 10; index++)
        {
            var score = index < player.Best.Count ? player.Best[index] : null;
            _bestGames.Rows.Add(index + 1, score?.Points.ToString("N0") ?? "—", score?.Kills.ToString() ?? "—", score is null ? "—" : "#" + score.Placement);
        }
        _bestGames.ClearSelection();
        _leaderboardStatus.Text = player.KdMatches < player.Matches
            ? $"Preseason 5  ·  Best 10 scores added together. K/D covers {player.KdMatches:N0}/{player.Matches:N0} matches; older results have no death data."
            : "Preseason 5  ·  Best 10 scores added together. K/D = kills ÷ deaths across all completed matches. ∞ means no deaths.";
    }

    private async Task RefreshLeaderboard(bool force = false)
    {
        if (_preview || _leaderboardLoading || _pageIndex != 4 || _session is null || (!force && DateTimeOffset.UtcNow < _leaderboardNextRefresh)) return;
        string mode = _leaderboardMode, token = _session.Token;
        _leaderboardLoading = true; _leaderboardRefresh.Enabled = false; _leaderboardStatus.Text = "Loading " + mode + " standings...";
        try
        {
            var board = await Get<LeaderboardView>("api/leaderboard/" + mode);
            if (IsDisposed || _session?.Token != token || _leaderboardMode != mode) return;
            DisplayLeaderboard(board); _leaderboardNextRefresh = DateTimeOffset.UtcNow.AddSeconds(15);
        }
        catch (Exception ex)
        {
            if (!IsDisposed && _session?.Token == token && _leaderboardMode == mode)
            {
                _leaderboardStatus.Text = "Leaderboard unavailable. " + Friendly(ex);
                _leaderboardNextRefresh = DateTimeOffset.UtcNow.AddSeconds(15);
            }
        }
        finally
        {
            _leaderboardLoading = false;
            if (!IsDisposed)
            {
                _leaderboardRefresh.Enabled = _session is not null;
                if (_leaderboardMode != mode && _session is not null) await RefreshLeaderboard(true);
            }
        }
    }

    private void DisplayLeaderboard(LeaderboardView board)
    {
        string? selected = _leaderboardPlayers.SelectedRows.Count > 0 ? (_leaderboardPlayers.SelectedRows[0].Tag as LeaderboardEntry)?.AccountId : null;
        _leaderboardPlayers.Rows.Clear();
        foreach (var player in board.Players)
        {
            int index = _leaderboardPlayers.Rows.Add(player.Position, player.Name, player.TotalScore.ToString("N0"), KillDeath(player));
            var row = _leaderboardPlayers.Rows[index]; row.Tag = player;
            row.Cells[1].ToolTipText = player.Tier;
            row.Cells[3].ToolTipText = $"{player.KdKills:N0} kills / {player.KdDeaths:N0} deaths in {player.KdMatches:N0} tracked matches";
            if (player.AccountId == _session?.AccountId) row.DefaultCellStyle.ForeColor = Color.FromArgb(235, 143, 128);
        }
        _leaderboardMe.Text = board.Me is { } me ? $"YOU  #{me.Position:N0}  ·  {me.TotalScore:N0} points  ·  K/D {KillDeath(me)}"
            : "Complete a " + board.Mode + " match to enter the leaderboard.";
        if (_leaderboardPlayers.Rows.Count > 0)
        {
            var row = _leaderboardPlayers.Rows.Cast<DataGridViewRow>().FirstOrDefault(row => ((LeaderboardEntry)row.Tag!).AccountId == selected) ?? _leaderboardPlayers.Rows[0];
            _leaderboardPlayers.CurrentCell = row.Cells[1]; row.Selected = true;
            ShowLeaderboardPlayer((LeaderboardEntry)row.Tag!);
        }
        else { ShowLeaderboardPlayer(null); _leaderboardStatus.Text = "No completed " + board.Mode + " matches yet. The first results will appear here."; }
    }

    private void PreviewLeaderboard()
    {
        string[] names = ["Alex", "Samuel", "Jamie", "Casey", "Morgan", "Jordan", "Taylor", "Riley"];
        var players = names.Select((name, index) => new LeaderboardEntry(index + 1, index == 1 ? "owner" : "preview-" + index, name,
            1838000 - index * 21000, index < 4 ? "Royalty 3" : "Master 2", 42 + index, (uint)(13 - index), (uint)(246 - index * 11),
            42 + index, (uint)(246 - index * 11), (uint)(29 + index * 2),
            Enumerable.Range(0, 10).Select(n => new LeaderboardScore(185000 - index * 2100 - n * 300, (uint)(n < 7 ? 1 : 2), 10 - n / 2)).ToArray())).ToArray();
        players = players.Select(player => player with { TotalScore = player.Best.Sum(score => score.Points) }).ToArray();
        DisplayLeaderboard(new(_leaderboardMode, "Preseason 5", players.Length, players, players[1]));
    }
}
