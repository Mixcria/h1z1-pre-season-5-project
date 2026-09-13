using System.Diagnostics;
using Cranberry.Launcher.Core;

namespace Cranberry.Launcher;

internal sealed partial class MainForm
{
    private void StartDoorSwingFix(Process game, string directory, GameLaunch launch)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (!await BidirectionalDoors.ApplyAfterStartup(game, directory, RecordUpdateStatus))
                    throw new InvalidOperationException("The game update could not initialize. Close the game and reopen the launcher.");
                for (int attempt = 0; ; attempt++)
                {
                    if (game.HasExited) return;
                    try
                    {
                        await Post<object>("api/client/doors-ready", new DoorClientReadyRequest(launch.Ticket, BidirectionalDoors.ProtocolVersion));
                        RecordUpdateStatus("Door update ready for multiplayer.");
                        return;
                    }
                    catch when (attempt < 2) { await Task.Delay(1000); }
                }
            }
            catch (Exception ex)
            {
                RecordUpdateStatus("Door update: " + ex.Message);
                if (!IsDisposed && IsHandleCreated)
                {
                    try { BeginInvoke((Action)(() => { if (!IsDisposed) _status.Text = "Game update: " + ex.Message; })); }
                    catch (InvalidOperationException) { } // The launcher may close during initialization.
                }
            }
        });
    }
}
