using Cranberry.Launcher.Core;

namespace Cranberry.Launcher;

internal sealed class ConversationForm : Form
{
    private readonly RichTextBox _history = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = LauncherTheme.Surface, ForeColor = LauncherTheme.Text, DetectUrls = false };
    private readonly TextBox _input = new() { Dock = DockStyle.Fill, MaxLength = 1000, BackColor = LauncherTheme.Field, ForeColor = LauncherTheme.Text };
    private readonly Label _status = new() { Dock = DockStyle.Fill, ForeColor = LauncherTheme.Muted, AutoEllipsis = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly Func<Task<Conversation>> _load;
    private readonly Func<MessageRequest, Task<DirectMessage>> _send;
    private readonly Func<long, Task> _read;
    private readonly string _actor, _friend, _name;
    private bool _polling, _sending;
    private long _lastId = -1, _readThrough;
    private MessageRequest? _pending;

    public ConversationForm(string actor, string friend, string name, Func<Task<Conversation>> load,
        Func<MessageRequest, Task<DirectMessage>> send, Func<long, Task> read)
    {
        _actor = actor; _friend = friend; _name = name; _load = load; _send = send; _read = read;
        Text = name + " — Cranberry messages"; ClientSize = new(520, 480); MinimumSize = new(380, 350);
        BackColor = LauncherTheme.Surface; ForeColor = LauncherTheme.Text; Font = LauncherTheme.Body(); StartPosition = FormStartPosition.CenterParent;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new(18) };
        grid.RowStyles.Add(new(SizeType.Percent, 100)); grid.RowStyles.Add(new(SizeType.Absolute, 42)); grid.RowStyles.Add(new(SizeType.Absolute, 44));
        var sendButton = new LauncherButton("SEND") { Dock = DockStyle.Right, Width = 82 };
        var compose = new Panel { Dock = DockStyle.Fill, Padding = new(0, 8, 0, 0) };
        compose.Controls.Add(_input); compose.Controls.Add(sendButton);
        grid.Controls.Add(_history, 0, 0); grid.Controls.Add(compose, 0, 1); grid.Controls.Add(_status, 0, 2); Controls.Add(grid);
        _input.AccessibleName = "Message"; _history.AccessibleName = "Conversation history";
        _status.Text = "Private messages are saved. Offline friends can read them when they return.";
        sendButton.Click += async (_, _) => await Send();
        _input.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await Send(); } };
        _timer.Tick += async (_, _) => await Poll(); Shown += async (_, _) => { _timer.Start(); await Poll(); _input.Focus(); };
        Activated += async (_, _) => await Poll();
        FormClosed += (_, _) => _timer.Dispose();
    }

    private async Task Send()
    {
        if (_sending || string.IsNullOrWhiteSpace(_input.Text)) return;
        _sending = true; _input.Enabled = false;
        string body = _input.Text.Trim();
        if (_pending?.Text != body) _pending = new(_friend, body, Guid.NewGuid().ToString("N"));
        try
        {
            await _send(_pending);
            if (IsDisposed) return;
            _input.Clear(); _pending = null; _status.Text = "Message sent."; await Poll();
        }
        catch (Exception ex) { if (!IsDisposed) _status.Text = ex.Message; }
        finally { _sending = false; if (!IsDisposed) { _input.Enabled = true; _input.Focus(); } }
    }

    private async Task Poll()
    {
        if (_polling || IsDisposed) return;
        _polling = true;
        try
        {
            var conversation = await _load();
            if (IsDisposed) return;
            long last = conversation.Messages.LastOrDefault()?.Id ?? 0;
            if (last != _lastId)
            {
                bool bottom = _history.SelectionStart + _history.SelectionLength >= _history.TextLength - 2;
                _history.Text = string.Join("\n\n", conversation.Messages.Select(m =>
                    $"{(m.From == _actor ? "You" : _name)}  ·  {m.SentAt.ToLocalTime():g}\n{m.Text}"));
                if (bottom || _lastId < 0) { _history.SelectionStart = _history.TextLength; _history.ScrollToCaret(); }
                _lastId = last;
            }
            long incoming = conversation.Messages.Where(m => m.To == _actor).Select(m => m.Id).DefaultIfEmpty().Max();
            if (ContainsFocus && incoming > _readThrough) { await _read(incoming); _readThrough = incoming; }
        }
        catch (Exception ex) { if (!IsDisposed) _status.Text = ex.Message; }
        finally { _polling = false; }
    }
}
