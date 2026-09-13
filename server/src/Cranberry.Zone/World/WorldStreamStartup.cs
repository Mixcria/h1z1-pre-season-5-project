namespace Cranberry.Zone.World;

/// <summary>
/// One descent start per world, and a drain barrier shared by overlapping startup bursts.
/// Landing can arrive while the descent burst is still being paid out in paced slices.
/// </summary>
internal sealed class WorldStreamStartup
{
    private int _generation = -1;
    private bool _descentArmed;
    private int _pendingBursts;

    public bool TryArmDescent(int generation)
    {
        EnterWorld(generation);
        if (_descentArmed) return false;
        _descentArmed = true;
        return true;
    }

    public void BeginBurst(int generation)
    {
        EnterWorld(generation);
        _pendingBursts++;
    }

    /// <returns>True only when the final startup burst for this world has drained.</returns>
    public bool CompleteBurst(int generation)
    {
        if (generation != _generation || _pendingBursts == 0) return false;
        return --_pendingBursts == 0;
    }

    private void EnterWorld(int generation)
    {
        if (_generation == generation) return;
        _generation = generation;
        _descentArmed = false;
        _pendingBursts = 0;
    }
}
