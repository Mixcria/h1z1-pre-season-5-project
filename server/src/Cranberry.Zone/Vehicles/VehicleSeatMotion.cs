namespace Cranberry.Zone.Vehicles;

/// <summary>
/// Native linear-speed magnitude used by the normal seat-change gate, separate from arrival-time
/// pose estimates used by other vehicle policies. August 140a3ca40 reads mask 0x10 into +0x15c;
/// 142339800 derives that value from the velocity vector, and 141594e10 permits magnitude <= 1.
/// </summary>
public sealed class VehicleSeatMotion
{
    private byte? _version;

    public float? Speed { get; private set; }
    public bool InvalidSpeed { get; private set; }

    /// <summary>Call only after the current simulator's movement has been accepted.</summary>
    public void Observe(byte version, float? speed, bool stopped)
    {
        if (_version != version)
        {
            Reset();
            _version = version;
        }

        if (speed is float reported)
        {
            InvalidSpeed = !float.IsFinite(reported) || reported < 0;
            Speed = InvalidSpeed ? null : reported;
        }
        else if (stopped)
        {
            // The native stop flag clears motion even when unchanged scalar fields are omitted.
            // A contradictory fresh nonzero/invalid magnitude above is never changed to zero.
            Speed = 0;
            InvalidSpeed = false;
        }
    }

    public bool AllowsSeatChange(float fallbackSpeed) => !InvalidSpeed
        && (Speed is float native ? native <= 1f : float.IsFinite(fallbackSpeed) && fallbackSpeed == 0f);

    public void Reset()
    {
        Speed = null;
        InvalidSpeed = false;
        _version = null;
    }
}
