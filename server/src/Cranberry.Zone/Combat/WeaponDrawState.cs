namespace Cranberry.Zone.Combat;

/// <summary>The latest draw wins; repeated selection never restarts its deadline.</summary>
public sealed class WeaponDrawState
{
    public ulong WeaponGuid { get; private set; }
    public long ReadyAtMs { get; private set; }
    private uint _unequipMs;

    public bool Select(ulong guid, uint equipMs, uint unequipMs, long nowMs)
    {
        if (guid == WeaponGuid) return false;
        ReadyAtMs = nowMs + _unequipMs + equipMs;
        WeaponGuid = guid;
        _unequipMs = unequipMs;
        return true;
    }

    public bool IsReady(long nowMs) => nowMs >= ReadyAtMs;

    public void Clear()
    {
        WeaponGuid = 0;
        ReadyAtMs = 0;
        _unequipMs = 0;
    }
}
