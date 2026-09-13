using Cranberry.Launcher.Core;

namespace Cranberry.Launcher;

internal sealed partial class MainForm
{
    private readonly PictureBox _profilePicture = new() { Size = new(72, 72), SizeMode = PictureBoxSizeMode.Zoom, BackColor = LauncherTheme.Surface };
    private readonly FlowLayoutPanel _profileControls = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new(0, 0, 0, 12) };
    private readonly FlowLayoutPanel _signInFields = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
    private readonly LauncherButton _savePicture = new("SAVE PICTURE") { Width = 140, Enabled = false };
    private readonly Dictionary<string, (string Version, Bitmap? Picture)> _avatars = [];
    private readonly Dictionary<string, ConversationForm> _chats = [];
    private readonly Dictionary<string, int> _unread = [];
    private LauncherState? _socialState;
    private string? _pendingPicture;
    private GameOverlayHotkey? _overlayHotkey;
    private bool _togglingOverlay;
    private PartyForm? _partyForm;
    private NotifyIcon? _inviteNotification;
    private readonly HashSet<string> _notifiedInvites = [];

    private void NotifyInvitations(LauncherState state)
    {
        _notifiedInvites.IntersectWith(state.Invites.Select(i => i.Id));
        var fresh = state.Invites.Where(i => _notifiedInvites.Add(i.Id)).ToArray();
        if (fresh.Length == 0 || _preview) return;
        if (_inviteNotification is null)
        {
            _inviteNotification = new NotifyIcon { Icon = Icon, Text = "Cranberry invitations", Visible = true };
            _inviteNotification.BalloonTipClicked += (_, _) => { WindowState = FormWindowState.Normal; Show(); ShowPage(1); Activate(); };
            _inviteNotification.DoubleClick += (_, _) => { WindowState = FormWindowState.Normal; Show(); ShowPage(1); Activate(); };
        }
        var invitation = fresh[^1];
        // In-game invitations play their cue in the game's own UI, including fullscreen.
        if (!(_game is { HasExited: false } && invitation.Kind == "GameParty"))
            System.Media.SystemSounds.Exclamation.Play();
        string description = invitation.Kind == "Friend" ? " sent you a friend request." : " invited you to their party.";
        _inviteNotification.ShowBalloonTip(5000, "Cranberry invitation", invitation.FromName + description + " Open Friends to accept.", ToolTipIcon.Info);
    }

    private void OpenParty()
    {
        if (_session is null) return;
        if (_partyForm is null)
        {
            _partyForm = new PartyForm(async (route, body) =>
            {
                await Action(async () =>
                {
                    await Post(route, body);
                    _status.Text = route == "api/party/queue" ? "Queued. Return to the game to join when it is ready."
                        : route == "api/party/leave" ? "Party left." : "Party updated.";
                });
                _partyForm?.ShowStatus(_status.Text);
            }, ToggleOverlay);
            _partyForm.FormClosed += (_, _) => _partyForm = null;
        }
        if (_socialState is { } state) _partyForm.ApplyState(state);
        _partyForm.Show(this); _partyForm.Activate();
    }

    private void BuildProfileControls(FlowLayoutPanel flow)
    {
        var title = TextLabel("PROFILE PICTURE", 13, heading: true); title.Width = 340;
        var choose = new LauncherButton("CHOOSE PICTURE") { Width = 150 };
        var remove = new LauncherButton("REMOVE") { Quiet = true, Width = 92 };
        _profileControls.Controls.Add(title); _profileControls.Controls.Add(_profilePicture);
        var hint = TextLabel("PNG or JPEG, up to 8 MB. A square crop is used in your lobby.", 9);
        hint.Width = 340; hint.Height = 34; _profileControls.Controls.Add(hint);
        _profileControls.Controls.Add(Row(choose, remove)); _profileControls.Controls.Add(_savePicture); flow.Controls.Add(_profileControls);
        choose.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Title = "Choose your profile picture", Filter = "Pictures (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg", CheckFileExists = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { _pendingPicture = ProfilePictures.Load(dialog.FileName); ShowProfilePicture(_pendingPicture); _savePicture.Enabled = true; }
            catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or IOException or InvalidOperationException)
            { _status.Text = ex is OutOfMemoryException or ArgumentException ? "That picture could not be opened. Choose another PNG or JPEG." : ex.Message; }
        };
        remove.Click += (_, _) => { _pendingPicture = ""; ShowProfilePicture(""); _savePicture.Enabled = true; };
        _savePicture.Click += async (_, _) => await Action(async () =>
        {
            if (_pendingPicture is null) return;
            await Post<AvatarView>("api/profile/avatar", new AvatarRequest(_pendingPicture));
            _pendingPicture = null; _savePicture.Enabled = false;
            _status.Text = "Profile picture saved. Your lobby updates automatically.";
        });
    }

    private void ShowProfilePicture(string pixels)
    {
        var previous = _profilePicture.Image;
        _profilePicture.Image = ProfilePictures.Decode(pixels); previous?.Dispose();
    }

    private async Task RefreshSocialPictures(LauncherState state)
    {
        var session = _session;
        var permitted = state.Friends.Select(p => p.AccountId).Append(state.Me.AccountId).ToHashSet();
        foreach (string removed in _avatars.Keys.Where(id => !permitted.Contains(id)).ToArray())
        { _avatars[removed].Picture?.Dispose(); _avatars.Remove(removed); }
        foreach (string removed in _chats.Keys.Where(id => !permitted.Contains(id)).ToArray()) _chats[removed].Close();
        foreach (var person in state.Friends.Prepend(state.Me))
        {
            if (_avatars.TryGetValue(person.AccountId, out var old) && old.Version == person.AvatarVersion) continue;
            var avatar = person.AvatarVersion.Length == 0 ? new AvatarView("", "") : await Get<AvatarView>("api/profile/" + Uri.EscapeDataString(person.AccountId) + "/avatar");
            if (_session != session || _closing) return;
            old.Picture?.Dispose();
            _avatars[person.AccountId] = (avatar.Version, ProfilePictures.Decode(avatar.Pixels));
            if (person.AccountId == state.Me.AccountId && _pendingPicture is null) ShowProfilePicture(avatar.Pixels);
        }
        _friends.Invalidate();
        var unread = await Get<MessageUnread[]>("api/messages/unread");
        if (_session != session || _closing) return;
        _unread.Clear(); foreach (var item in unread) _unread[item.AccountId] = item.Count;
        ApplyState(state);
    }

    private void OpenConversation()
    {
        if (_session is null || _friends.SelectedItem is not Item friend) return;
        if (_chats.TryGetValue(friend.Id, out var existing)) { existing.Show(); existing.Activate(); return; }
        var form = new ConversationForm(_session.AccountId, friend.Id, friend.Text,
            () => Get<Conversation>("api/messages/" + Uri.EscapeDataString(friend.Id)),
            request => Post<DirectMessage>("api/messages", request),
            through => Post("api/messages/read", new ReadMessagesRequest(friend.Id, through)));
        _chats[friend.Id] = form;
        form.FormClosed += (_, _) => _chats.Remove(friend.Id);
        form.Show(this);
    }

    private void ClearSocial()
    {
        _partyForm?.Close();
        _inviteNotification?.Dispose(); _inviteNotification = null; _notifiedInvites.Clear();
        foreach (var chat in _chats.Values.ToArray()) chat.Close();
        foreach (var avatar in _avatars.Values) avatar.Picture?.Dispose();
        _avatars.Clear(); _unread.Clear(); _socialState = null; _pendingPicture = null;
        _savePicture.Enabled = false; ShowProfilePicture("");
    }

    private void StartOverlayHotkey(int processId)
    {
        _overlayHotkey?.Dispose();
        _overlayHotkey = new GameOverlayHotkey(processId, _gameInput!, () => BeginInvoke(async () => await ToggleOverlay()));
    }

    private async Task ToggleOverlay()
    {
        if (_togglingOverlay || _session is null) return;
        if (_game is not { HasExited: false }) { _partyForm?.ShowStatus("Launch the game and reach the menu first."); return; }
        _togglingOverlay = true;
        try
        {
            await Post("api/overlay/toggle", new TargetRequest(Guid.NewGuid().ToString("N")));
            _status.Text = "Friends overlay toggled. Return to the game to see it.";
        }
        catch (Exception ex) { _status.Text = "Friends overlay: " + Friendly(ex); }
        finally { _togglingOverlay = false; _partyForm?.ShowStatus(_status.Text); }
    }
}
