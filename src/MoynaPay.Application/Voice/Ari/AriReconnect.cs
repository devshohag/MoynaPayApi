namespace MoynaPay.Application.Voice.Ari;

/// <summary>
/// How long to wait before trying the ARI socket again.
///
/// A dropped connection is normal, not exceptional: Asterisk restarts, networks blink. What
/// must not happen is a tight reconnect loop against a box that is down - that turns one
/// outage into two.
///
/// Pure, so the whole curve is checked in a millisecond instead of over five minutes.
/// </summary>
public static class AriReconnect
{
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Thirty seconds, not five minutes. Every second the socket is down is a second of
    /// keypresses nobody hears, so the ceiling is set by how long a customer will hold the
    /// phone, not by how polite we are being to the server.
    /// </summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A connection that lasted this long was not a failing connection. Resetting the
    /// backoff after one means a nightly Asterisk restart does not leave us waiting half a
    /// minute every single night.
    /// </summary>
    public static readonly TimeSpan HealthyFor = TimeSpan.FromMinutes(1);

    public static TimeSpan Delay(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0) return InitialDelay;

        // Capped at ten doublings before the Min, so the multiplication cannot overflow on a
        // process that has been failing for a week.
        var scaled = InitialDelay.TotalMilliseconds
            * Math.Pow(2, Math.Min(consecutiveFailures - 1, 10));

        return TimeSpan.FromMilliseconds(Math.Min(scaled, MaxDelay.TotalMilliseconds));
    }

    /// <summary>
    /// Whether the backoff should start again from the beginning, given how long the
    /// connection that just dropped had been up.
    /// </summary>
    public static bool ResetsBackoff(bool wasConnected, TimeSpan heldFor) =>
        wasConnected && heldFor > HealthyFor;
}
