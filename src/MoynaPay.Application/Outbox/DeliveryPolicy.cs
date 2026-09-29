namespace MoynaPay.Application.Outbox;

/// <summary>What the shop's server said, or what stopped us reaching it.</summary>
/// <param name="StatusCode">Null when the request never got an answer at all.</param>
public readonly record struct DeliveryAttempt(int? StatusCode, string? Reason)
{
    /// <summary>Anything in the 200s. A shop that answers 204 has heard us.</summary>
    public bool Ok => StatusCode is >= 200 and < 300;

    public static DeliveryAttempt Answered(int statusCode, string? reason = null) =>
        new(statusCode, reason);

    public static DeliveryAttempt Unreachable(string reason) => new(null, reason);
}

public enum DeliveryState
{
    /// <summary>The shop has it. Nothing more to do.</summary>
    Delivered = 0,

    /// <summary>Try again later. NextAttemptAt says when.</summary>
    Retry = 1,

    /// <summary>Stop. The shop never heard this one and nothing we do will change that.</summary>
    Dead = 2,
}

public readonly record struct DeliveryPlan(
    DeliveryState State, DateTimeOffset? NextAttemptAt, string? Reason);

/// <summary>
/// How hard we try, and when we stop.
///
/// Pure: given the attempts so far, what came back and the time, it says what happens
/// next. No clock of its own, no store, no HTTP - which is what lets the whole retry
/// schedule be checked in a millisecond instead of over a day and a half.
/// </summary>
public static class DeliveryPolicy
{
    /// <summary>
    /// The gap before each retry, in order.
    ///
    /// Ten seconds first because most failures are a blip - a restart, a deploy, a dropped
    /// connection - and a shop that was down for eight seconds should not wait a minute to
    /// hear about a paid order. The tail is long because the failures that survive the
    /// first few minutes are usually somebody's expired certificate or a bill they forgot,
    /// and those are fixed in hours, not seconds.
    /// </summary>
    public static readonly TimeSpan[] Gaps =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(24),
    ];

    /// <summary>Eight tries over about thirty-three hours, then we stop.</summary>
    public static int MaxAttempts => Gaps.Length + 1;

    /// <summary>
    /// How long a dispatcher may hold a claimed row before another one may take it.
    ///
    /// Generously longer than the request timeout, because the thing it protects against
    /// is a worker that died, not one that is being slow. Too short and two workers deliver
    /// the same message while the first is still waiting for the shop to answer.
    /// </summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    public static DeliveryPlan Plan(int attemptsBefore, DeliveryAttempt attempt, DateTimeOffset now)
    {
        if (attempt.Ok) return new DeliveryPlan(DeliveryState.Delivered, null, null);

        // 410 Gone is the one answer that means "stop asking". Everything else in the 4xx
        // range is retried: a shop that has misconfigured its route answers 404 while it is
        // broken and 200 once somebody fixes it, and giving up on the first 404 would throw
        // away every order placed during the outage.
        if (attempt.StatusCode == 410)
        {
            return new DeliveryPlan(DeliveryState.Dead, null, "endpoint gone (410)");
        }

        var attempts = attemptsBefore + 1;
        var because = attempt.Reason ?? Describe(attempt.StatusCode);

        if (attempts >= MaxAttempts)
        {
            return new DeliveryPlan(
                DeliveryState.Dead, null, $"gave up after {attempts} attempts: {because}");
        }

        return new DeliveryPlan(DeliveryState.Retry, now + Gaps[attempts - 1], because);
    }

    private static string Describe(int? statusCode) =>
        statusCode is null ? "no answer" : $"HTTP {statusCode}";
}
