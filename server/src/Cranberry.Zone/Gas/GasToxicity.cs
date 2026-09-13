namespace Cranberry.Zone.Gas;

/// <summary>
/// One player's toxicity meter — resource <b>611</b>, the <c>m_toxicity</c> <c>ResourceBar</c> the
/// August client already draws beside health and fuel in <c>HudPlayerResourcesWindow.gfx</c>
/// (docs/118 §4, D279).
/// <para>
/// <b>Every rate here is the client's own</b> (<c>out/data_aug/Resources.txt:145</c>): id 611,
/// type 75, <c>MAX_VALUE</c> 180 000, <c>REGEN_PER_MS</c> 1 on a <c>REGEN_TICK_MSEC</c> of 1 000 =
/// 1 000 units a second = <b>180 s from empty to full</b>. The one number that is not the client's
/// is the drain, because the row's own <c>BURN_PER_MSEC</c> is 0 and
/// <c>FLAG_INIT_WITH_DISABLED_BURN</c> is 1: a client-side drain off those columns would drain
/// nothing at all. D279 mirrors the fill.
/// </para>
/// <para>
/// <b>Why the server holds the value rather than only arming the flags.</b> The row ships with
/// <b>both</b> <c>FLAG_INIT_WITH_DISABLED_REGEN</c> and <c>FLAG_INIT_WITH_DISABLED_BURN</c> set, so
/// nothing moves until a server says so — but the burn it would enable is zero, so "arm the two
/// flags and let the client simulate" can fill the meter and can never empty it. One
/// <c>8d ResourceEvent</c> type 3 per gas tick <i>on which the value actually changed</i> is
/// 101 bytes, costs nothing in the steady state (a player at 0 outside the gas produces no traffic
/// at all), and is the same carrier the health and stamina rows already use
/// (<c>CharacterResourceUpdate</c>, <c>FUN_140ce52c0</c> — a complete row updates an existing local
/// resource or creates its missing <c>Resources.PlayerResourceDataSource</c> entry, which is what
/// arms the bar in the first place: 611 is not in <c>CharacterResource.Starter</c>).
/// </para>
/// <para>
/// This type is a value, not a service: it does no I/O and knows nothing about a connection.
/// <c>ZoneService</c> maps <see cref="Tick"/>'s answer onto the one send.
/// </para>
/// </summary>
public struct GasToxicity
{
    private double _exactValue;

    /// <summary>The meter's current value, 0..<see cref="GasSettings.ToxicityMaxValue"/>.</summary>
    public uint Value { get; private set; }

    /// <summary>The value the last <c>8d</c> told the client, so a tick that changes nothing sends nothing.</summary>
    public uint Sent { get; private set; }

    /// <summary>True once the arming <c>8d</c> has gone out for this match.</summary>
    public bool Armed { get; private set; }

    /// <summary>Fraction of the full meter, 0..1 — what the client's own <c>ResourceBar</c> shows.</summary>
    public readonly float FractionOf(GasSettings settings) =>
        settings is null || settings.ToxicityMaxValue == 0
            ? 0f
            : Value / (float)settings.ToxicityMaxValue;

    /// <summary>Opens a match: an empty meter that has not been armed yet.</summary>
    public void Reset()
    {
        _exactValue = 0;
        Value = 0;
        Sent = 0;
        Armed = false;
    }

    /// <summary>
    /// Marks the arming send as done. <see cref="Sent"/> becomes <see cref="Value"/>, so the very
    /// next tick sends only if the meter has actually moved.
    /// </summary>
    public void MarkArmed()
    {
        Armed = true;
        Sent = Value;
    }

    /// <summary>Copies the authoritative controller value without losing this connection's send state.</summary>
    public bool Synchronize(uint value)
    {
        _exactValue = value;
        Value = value;
        return Value != Sent;
    }

    /// <summary>
    /// Advances the meter by one gas tick and says whether the client has to be told.
    /// </summary>
    /// <param name="settings">The rates; nothing is read from anywhere else.</param>
    /// <param name="inGas">True when the player is outside the active safe circle.</param>
    /// <param name="elapsedMs">Milliseconds since the previous tick — <c>GasSettings.TickPeriodMs</c> in practice.</param>
    /// <returns>True when <see cref="Value"/> moved away from <see cref="Sent"/>.</returns>
    public bool Tick(GasSettings settings, bool inGas, uint elapsedMs)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (elapsedMs == 0)
        {
            return false;
        }

        double seconds = elapsedMs / 1000d;
        double delta = inGas
            ? settings.ToxicityFillPerSecond * seconds
            : -(settings.ToxicityDrainPerSecond * seconds);

        // Retain fractional units between updates. Rounding each increment makes a lower fill
        // rate depend on host cadence and can prevent the meter from ever reaching full.
        _exactValue = Math.Clamp(_exactValue + delta, 0d, settings.ToxicityMaxValue);
        Value = (uint)Math.Round(_exactValue, MidpointRounding.AwayFromZero);
        return Value != Sent;
    }

    /// <summary>Records that the current value has been put on the wire.</summary>
    public void MarkSent() => Sent = Value;
}
