using Cranberry.Launcher.Core;

namespace Cranberry.Launcher;

internal static class Program
{
    internal static LocalEdition? Local { get; private set; }
    internal static string SettingsPath { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cranberry", "launcher.json");
    private static string PackagePath = Path.Combine(AppContext.BaseDirectory, "launcher.json");
    internal static LauncherSettings Settings()
    {
        return LauncherProfile.Load(SettingsPath, PackagePath);
    }
    internal static void Save(LauncherSettings settings)
    {
        LauncherProfile.Save(SettingsPath, settings);
    }

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        bool render = args.Length >= 2 && args[0] is "--render" or "--render-setup";
        bool preview = args.Length > 0 && args[0] == "--preview";
        try
        {
            bool localMode = !render && !preview && (args.Length == 0 || args.FirstOrDefault() == "--local-data")
                && File.Exists(Path.Combine(AppContext.BaseDirectory, "local-edition.json"));
            if (localMode)
            {
                if (args.Length != 0 && args.Length != 2) throw new ArgumentException("Use --local-data followed by a private data folder.");
                Local = LocalEdition.Prepare(AppContext.BaseDirectory, args.Length == 2 ? args[1] : null);
                SettingsPath = Local.ProfilePath;
                PackagePath = Local.PackageProfilePath;
            }
            if (args.FirstOrDefault() == "--profile")
            {
                if (args.Length != 2) throw new ArgumentException("Use --profile followed by a launcher profile file.");
                PackagePath = Path.GetFullPath(args[1]);
                SettingsPath = LauncherProfile.SettingsPathForProfile(Path.GetDirectoryName(SettingsPath)!, PackagePath);
            }
            bool setup = args.FirstOrDefault() == "--render-setup" ||
                Path.GetFileNameWithoutExtension(Environment.ProcessPath!).EndsWith("Setup", StringComparison.OrdinalIgnoreCase);
            Form form = render && args.ElementAtOrDefault(2) == "password" ? new ChangePasswordForm("Samuel", _ => Task.CompletedTask)
                : setup ? new SetupForm() : render && args.ElementAtOrDefault(2) == "party"
                ? new PartyForm((_, _) => Task.CompletedTask, () => Task.CompletedTask) : new MainForm(render || preview
                ? new LauncherSettings { ServerUrl = "https://server.example/" } : Settings());
            if (form is PartyForm party) party.ApplyState(new(new("a", "Alice", true, "Menu"), [], [],
                new("party", "a", "Fives", Enumerable.Range(0, 5).Select(i => new LobbyMember(
                    i == 0 ? "a" : i.ToString(), new[] { "Alice", "Bobby", "Casey", "Jamie", "Morgan" }[i], true, true, "Menu")).ToArray())));
            if (form is MainForm localLauncher && !render && !preview && args.FirstOrDefault() == "--local-host")
            {
                if (args.Length != 2) throw new ArgumentException("Use --local-host followed by the local server folder.");
                localLauncher.EnableLocalHost(Path.GetFullPath(args[1]));
            }
            if (form is MainForm launcher && (render || preview))
                launcher.RenderPreview(args.Length > 2 ? args[2] : "game");
            if (form is MainForm community && Local is not null)
                community.EnableLocalEdition(Local);
            if (form is MainForm updatable && Local is null && !render && !preview && args.FirstOrDefault() != "--local-host")
                updatable.EnableLauncherUpdates(args);
            if (render)
            {
                form.ShowInTaskbar = false;
                form.Opacity = 0;
                if (!setup && args.Length >= 5) form.ClientSize = new(int.Parse(args[3]), int.Parse(args[4]));
                form.Shown += (_, _) =>
                {
                    form.BeginInvoke(() =>
                    {
                        form.PerformLayout();
                        using var image = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                        image.Save(Path.GetFullPath(args[1]));
                        form.Close();
                    });
                };
            }
            Application.Run(form);
        }
        catch (Exception ex)
        {
            if (render) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            else MessageBox.Show(ex.Message, "Cranberry Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { Local?.Dispose(); }
    }
}
