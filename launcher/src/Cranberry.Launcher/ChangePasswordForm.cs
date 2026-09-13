using Cranberry.Launcher.Core;

namespace Cranberry.Launcher;

internal sealed class ChangePasswordForm : Form
{
    private bool _saving;

    public ChangePasswordForm(string account, Func<ChangePasswordRequest, Task> save)
    {
        Text = "Change password"; ClientSize = new(460, 440); FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
        BackColor = LauncherTheme.Background; ForeColor = LauncherTheme.Text; Font = LauncherTheme.Body();
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(24), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        Controls.Add(flow);
        flow.Controls.Add(new Label { Text = PasswordPolicy.Guidance, Width = 405, Height = 62 });
        TextBox Field(string label)
        {
            flow.Controls.Add(new Label { Text = label, Width = 405, Height = 21, Margin = new(0, 8, 0, 0) });
            var field = new TextBox { Width = 405, UseSystemPasswordChar = true, MaxLength = 128,
                BackColor = LauncherTheme.Field, ForeColor = LauncherTheme.Text, BorderStyle = BorderStyle.FixedSingle,
                AccessibleName = label, Margin = new(0, 0, 0, 5) };
            flow.Controls.Add(field); return field;
        }
        var current = Field("Current password");
        var next = Field("New password");
        var confirm = Field("Confirm new password");
        var status = new Label { Width = 405, Height = 54, ForeColor = LauncherTheme.Muted, Margin = new(0, 10, 0, 4) };
        flow.Controls.Add(status);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var submit = new LauncherButton("SAVE PASSWORD") { Width = 170, Primary = true };
        var cancel = new LauncherButton("CANCEL") { DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(submit); buttons.Controls.Add(cancel); flow.Controls.Add(buttons);
        AcceptButton = submit; CancelButton = cancel;
        submit.Click += async (_, _) =>
        {
            if (_saving) return;
            try
            {
                if (current.Text.Length == 0) throw new InvalidOperationException("Enter your current password.");
                PasswordPolicy.Validate(next.Text, account);
                if (next.Text != confirm.Text) throw new InvalidOperationException("The new passwords do not match.");
                _saving = true; submit.Enabled = cancel.Enabled = current.Enabled = next.Enabled = confirm.Enabled = false;
                status.ForeColor = LauncherTheme.Muted; status.Text = "Saving your password...";
                await save(new(current.Text, next.Text));
                _saving = false; DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { status.ForeColor = Color.Salmon; status.Text = ex.Message; }
            finally { _saving = false; submit.Enabled = cancel.Enabled = current.Enabled = next.Enabled = confirm.Enabled = true; }
        };
        FormClosing += (_, e) => e.Cancel = _saving;
        FormClosed += (_, _) => { current.Clear(); next.Clear(); confirm.Clear(); };
    }
}
