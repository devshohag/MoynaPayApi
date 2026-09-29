namespace MoynaPay.Infrastructure;

/// <summary>
/// Which merchant the work in front of us belongs to.
///
/// The database's query filter reads this. It is set once, by the signing middleware,
/// after a request has been attributed to a key - and never again, so nothing further down
/// the pipeline can widen what that request is allowed to see.
///
/// Null means "no merchant in particular", and that passes the filter rather than matching
/// nothing. A worker draining every merchant's outbox has no merchant of its own, and a
/// filter that refused it would leave every queue undelivered. Isolation on the request
/// path comes from the API setting this before anything is read, not from the default.
/// </summary>
public interface ITenantContext
{
    Guid? Current { get; }

    void Set(Guid merchantId);
}

public sealed class TenantContext : ITenantContext
{
    public Guid? Current { get; private set; }

    /// <summary>
    /// Write once. A second call is a bug - it would mean one request being attributed to
    /// two merchants - and it is louder as an exception here than as one shop reading
    /// another's customers.
    /// </summary>
    public void Set(Guid merchantId)
    {
        if (Current is { } already && already != merchantId)
        {
            throw new InvalidOperationException(
                $"This request is already attributed to merchant {already}.");
        }

        Current = merchantId;
    }
}
