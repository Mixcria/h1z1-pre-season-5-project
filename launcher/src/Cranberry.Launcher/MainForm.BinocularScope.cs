using Cranberry.Launcher.Core;
using System.Diagnostics;

namespace Cranberry.Launcher;

internal sealed partial class MainForm
{
    private static void StartBinocularScopeFix(Process game, string directory)
    {
        _ = Task.Run(() => BinocularScopeFix.ApplyAfterStartup(game, directory, message =>
        {
            try
            {
                string logRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cranberry", "logs");
                Directory.CreateDirectory(logRoot);
                File.AppendAllText(Path.Combine(logRoot, "client-gameplay.log"), $"{DateTime.UtcNow:O} pid={game.Id} {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }));
    }
}
