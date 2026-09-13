using Cranberry.Launcher.Core;
using System.Diagnostics;

namespace Cranberry.Launcher;

internal sealed partial class MainForm
{
    private static void StartLootReloadFix(Process game, string directory)
    {
        // Disk hashing and startup observation do not block the launcher UI or game tunnel.
        void Log(string message)
        {
            try
            {
                string logRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cranberry", "logs");
                Directory.CreateDirectory(logRoot);
                File.AppendAllText(Path.Combine(logRoot, "client-gameplay.log"), $"{DateTime.UtcNow:O} pid={game.Id} {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        _ = Task.Run(() => LootReloadFix.ApplyAfterStartup(game, directory, Log));
        _ = Task.Run(() => ThrowableCleanupFix.ApplyAfterStartup(game, directory, Log));
    }
}
