namespace MoynaPay.Domain.Common;

/// <summary>
/// Every merchant-owned row in the system.
///
/// Same shape as CCaaS, on purpose: one person maintains both, and two different audit
/// conventions in two services he switches between is how a column gets written in one
/// and read in the other.
///
/// The tenant here is the merchant. A shop is the boundary - its orders, its credentials,
/// its call scripts - and nothing crosses it.
/// </summary>
public abstract class BaseEntity : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Soft delete. A merchant's order history is not ours to destroy.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// The optimistic concurrency token. Bumped on every update and compared in the WHERE
    /// clause of that update, so two writers editing one row cannot both win: the second
    /// one matches no row and is told, rather than silently overwriting a decision it
    /// never saw.
    ///
    /// Postgres offers xmin for this at the cost of no column at all. The provider's
    /// helper for it is not in Npgsql 9, so this is one bigint instead. It behaves the
    /// same; the one thing it asks in return is that raw SQL updates bump it themselves.
    ///
    /// Named RowVersion, not Version, because ParserTemplate already has a Version of its
    /// own and it means something else entirely.
    /// </summary>
    public long RowVersion { get; set; }

    /// <summary>
    /// The same column as <see cref="TenantId"/>, under the name the payment module's
    /// code already uses.
    ///
    /// The tenant in this product is the merchant, so the two words mean one thing. The
    /// payment code came over from YoPay, where it was written against MerchantId and
    /// where it has been running; rewriting several hundred references to rename a
    /// concept that did not change would risk a lot to gain nothing.
    ///
    /// It is not a second column, and phase 2 maps it as ignored. TenantId stays the one
    /// the query filter reads, because a filter that misses a base class is one forgotten
    /// WHERE away from one shop reading another's customers.
    /// </summary>
    public Guid MerchantId
    {
        get => TenantId;
        set => TenantId = value;
    }
}

/// <summary>
/// Thrown when a caller attempts something the domain forbids - an illegal invoice
/// transition, for example. Distinct from infrastructure failures so the API can map it
/// to 409 or 422 rather than 500.
/// </summary>
public sealed class DomainException(string message) : Exception(message);

/// <summary>
/// Read by the global query filter, so a query cannot cross merchants by accident.
///
/// The filter is the defence, not the discipline of whoever writes the next repository
/// method: one forgotten WHERE is one shop reading another's customers and phone numbers,
/// and that is the failure this product cannot survive.
/// </summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}
