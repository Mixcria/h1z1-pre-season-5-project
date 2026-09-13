namespace Cranberry.Transport;

/// <summary>
/// Smoothed RTT plus four deviations, following RFC 6298's estimator (not its TCP timeout
/// policy). This game UDP transport bounds the timeout to 100..2400 ms. The caller supplies
/// only unambiguous, advancing acknowledgements; retransmitted flights are never sampled.
/// </summary>
internal sealed class RoundTripEstimator
{
    public long Samples { get; private set; }
    public double SmoothedMs { get; private set; }
    public double VariationMs { get; private set; }

    public void Observe(long elapsedMs)
    {
        if (elapsedMs < 0) return;
        double sample = Math.Max(1, elapsedMs);
        if (Samples == 0)
        {
            SmoothedMs = sample;
            VariationMs = sample / 2;
        }
        else
        {
            VariationMs += .25 * (Math.Abs(SmoothedMs - sample) - VariationMs);
            SmoothedMs += .125 * (sample - SmoothedMs);
        }
        Samples++;
    }

    public int TimeoutMs(int initial, int minimum, int maximum) => Samples == 0
        ? initial : (int)Math.Clamp(Math.Ceiling(SmoothedMs + Math.Max(20, 4 * VariationMs)), minimum, maximum);
}
