using System.Diagnostics;

namespace Cranberry.Launcher;

internal sealed class SetupForm : Form
{
    public SetupForm()
    {
        Text = "Install Cranberry Launcher";
        ClientSize = new(620, 370); StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        Icon = LauncherTheme.Icon(); BackColor = LauncherTheme.Background; ForeColor = LauncherTheme.Text;
        Font = LauncherTheme.Body(); AutoScaleMode = AutoScaleMode.Dpi;
        Controls.Add(new ArtworkPanel { Location = new(360, 0), Size = new(260, 370), SideCrop = true });
        Controls.Add(new Label { Text = "CRANBERRY", Font = LauncherTheme.Heading(27), Location = new(28, 29), Size = new(310, 57) });
        Controls.Add(new Label { Text = "H1Z1: KING OF THE KILL", ForeColor = LauncherTheme.Muted, Location = new(30, 88), Size = new(310, 25) });
        Controls.Add(new Label { Text = "Install the launcher to play the\nearly August 2017 client.\n\nYour game download starts after sign-in.",
            ForeColor = LauncherTheme.Muted, Location = new(30, 147), Size = new(305, 95) });
        var button = new LauncherButton("INSTALL & OPEN") { Location = new(30, 268), Size = new(296, 49), Primary = true, Font = LauncherTheme.Heading(16) };
        Controls.Add(new Label { Text = "Installs for your Windows account.", ForeColor = LauncherTheme.Muted, Location = new(30, 328), Size = new(305, 25), Font = LauncherTheme.Body(8.5f) });
        button.Click += (_, _) =>
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Cranberry");
                Directory.CreateDirectory(folder);
                string destination = Path.Combine(folder, "Cranberry.Launcher.exe");
                File.Copy(Environment.ProcessPath!, destination, true);
                using (var license = new StreamReader(LauncherTheme.Resource("Oswald-OFL.txt")))
                    File.WriteAllText(Path.Combine(folder, "Oswald-OFL.txt"), license.ReadToEnd());
                Directory.CreateDirectory(Path.Combine(folder, "Notices"));
                foreach (string notice in new[] { "Concentus-LICENSE.txt", "NAudio-LICENSE.txt" })
                {
                    using var license = new StreamReader(LauncherTheme.Resource(notice));
                    File.WriteAllText(Path.Combine(folder, "Notices", notice), license.ReadToEnd());
                }
                string config = Path.Combine(AppContext.BaseDirectory, "launcher.json");
                if (File.Exists(config)) File.Copy(config, Path.Combine(folder, "launcher.json"), true);
                Type shellType = Type.GetTypeFromProgID("WScript.Shell")!;
                dynamic shell = Activator.CreateInstance(shellType)!;
                foreach (string baseFolder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
                {
                    dynamic shortcut = shell.CreateShortcut(Path.Combine(baseFolder, "Cranberry Launcher.lnk"));
                    shortcut.TargetPath = destination; shortcut.WorkingDirectory = folder;
                    shortcut.Description = "H1Z1 August 2017 — Cranberry"; shortcut.Save();
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
                }
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
                Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true });
                Close();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); }
        };
        Controls.Add(button);
    }
}
