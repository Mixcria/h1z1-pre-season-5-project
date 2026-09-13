using Cranberry.Launcher.Core;
using System.Diagnostics;

namespace Cranberry.Launcher;

internal sealed partial class MainForm
{
    private static void StartOwnBulletTracerFix(Process game, string directory)
    {
        _ = Task.Run(() => OwnBulletTracers.ApplyAfterStartup(game, directory, message =>
        {
            try
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cranberry", "logs");
                Directory.CreateDirectory(root);
                File.AppendAllText(Path.Combine(root, "client-gameplay.log"), $"{DateTime.UtcNow:O} pid={game.Id} {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }));
    }
}
