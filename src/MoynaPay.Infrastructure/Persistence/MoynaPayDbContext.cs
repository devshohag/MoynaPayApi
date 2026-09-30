using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MoynaPay.Application.Abstractions;
using MoynaPay.Domain.Common;
using MoynaPay.Domain.Merchants;
using MoynaPay.Domain.Orders;
using MoynaPay.Domain.Payments;
using MoynaPay.Domain.Voice;

namespace MoynaPay.Infrastructure.Persistence;

/// <summary>
/// The database.
///
/// Three schemas, one per part of the product that owns tables today: merchants, orders,
/// payments. A fourth, voice, arrives with the call engine.
///
/// What is mapped here is what the running code uses. The voice entities and most of the
/// payment ones came over from the other two repositories and no service reads them yet;
/// mapping them now would be guessing at a schema for code that has not been written, and
/// a migration is an expensive place to guess. They are mapped in the phases that use
/// them.
///
/// Two things below are defences rather than conveniences, and both are applied to every
/// entity by reflection rather than one at a time. Applying them by hand works until the
/// day somebody adds an entity and forgets, which is exactly the day it matters.
/// </summary>
public sealed class MoynaPayDbContext(
    DbContextOptions<MoynaPayDbContext> options, ITenantContext tenant) : DbContext(options)
{
    public DbSet<Merchant> Merchants => Set<Merchant>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<ApiCredential> ApiCredentials => Set<ApiCredential>();
    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();
    public DbSet<RequestNonce> RequestNonces => Set<RequestNonce>();
    public DbSet<RateLimitHit> RateLimitHits => Set<RateLimitHit>();

    public DbSet<AppOtpChallenge> AppOtpChallenges => Set<AppOtpChallenge>();
    public DbSet<AppToken> AppTokens => Set<AppToken>();
    public DbSet<AppDevicePairingToken> AppDevicePairingTokens => Set<AppDevicePairingToken>();
    public DbSet<AppDevice> AppDevices => Set<AppDevice>();

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderEvent> OrderEvents => Set<OrderEvent>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<WorkflowSession> WorkflowSessions => Set<WorkflowSession>();
    public DbSet<WorkflowAction> WorkflowActions => Set<WorkflowAction>();

    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<SipTrunk> SipTrunks => Set<SipTrunk>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.HasDefaultSchema("moynapay");

        // Kept out of the model entirely, and this has to be said here rather than as an
        // Ignore on the navigation.
        //
        // DbSet<Invoice> is processed before OnModelCreating runs, so by the time a
        // configuration ignores Invoice.Wallet the convention has already walked that
        // navigation, found Wallet, walked Wallet.Devices and found Device. Ignoring the
        // navigation afterwards removes the navigation; the entity types it dragged in
        // stay, and turn up as tables in the default schema with no configuration behind
        // them - which is exactly what the first migration produced.
        //
        // These are the rest of the payment module, ported from YoPay. No service reads
        // them yet. They are mapped in phases 32 to 35, with their own schema and their
        // own indexes, by somebody who knows what the matcher needs.
        builder.Ignore<Wallet>();
        builder.Ignore<PaymentSession>();
        builder.Ignore<Device>();
        builder.Ignore<DevicePairingToken>();
        builder.Ignore<ParsedTransaction>();
        builder.Ignore<PaymentClaim>();
        builder.Ignore<PaymentMatch>();
        builder.Ignore<RawEvent>();
        builder.Ignore<ParserTemplate>();

        builder.ApplyConfigurationsFromAssembly(typeof(MoynaPayDbContext).Assembly);

        foreach (var entity in builder.Model.GetEntityTypes().ToList())
        {
            var clr = entity.ClrType;
            var entry = builder.Entity(clr);

            foreach (var property in entity.GetProperties().ToList())
            {
                var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;

                // Enums are stored as their names, not their numbers. The number is
                // smaller and faster and wrong: insert a status into the middle of an enum
                // and every existing row means something else, silently, with no migration
                // that could have caught it.
                if (type.IsEnum)
                {
                    entry.Property(property.Name).HasConversion<string>().HasMaxLength(32);
                    continue;
                }

                // Postgres keeps timestamptz as an instant and stores no offset, so Npgsql
                // refuses any DateTimeOffset whose offset is not zero rather than
                // discarding it quietly. Everything upstream normalises to UTC; this says
                // so once, in one place.
                if (type == typeof(DateTimeOffset))
                {
                    property.SetColumnType("timestamptz");
                    continue;
                }

                // Money. 18,2 rather than a float, because a float cannot hold 1250.10
                // exactly and a payment that is out by a hundredth matches nothing.
                if (type == typeof(decimal))
                {
                    entry.Property(property.Name).HasPrecision(18, 2);
                    continue;
                }

                // Anything a service already treats as JSON is stored as jsonb, so it can
                // be queried and indexed later without a migration that rewrites the table.
                if (type == typeof(string)
                    && property.Name.EndsWith("Json", StringComparison.Ordinal))
                {
                    property.SetColumnType("jsonb");
                }
            }

            if (!typeof(BaseEntity).IsAssignableFrom(clr)) continue;

            // MerchantId is the same column as TenantId under the name the payment code
            // already uses. Mapping it would produce a second column that drifts from the
            // first, and the day they disagree is the day a shop reads someone else's rows.
            entry.Ignore(nameof(BaseEntity.MerchantId));

            // Optimistic concurrency, on every entity, always.
            entry.Property(nameof(BaseEntity.RowVersion)).IsConcurrencyToken();

            // Soft delete plus merchant isolation, on every entity, always.
            entry.HasQueryFilter(BuildFilter(clr));
        }

        // The four types phases 12 to 17 put in the Application assembly are not
        // BaseEntity, so the loop above did not reach them. Their filters are written out
        // here rather than left off: a table with a MerchantId and no filter is one
        // forgotten WHERE away from one merchant reading another's tokens.
        //
        // A null tenant passes, as everywhere else. It has to: a token is looked up in
        // order to find out which merchant is calling, so the merchant is not known yet.
        builder.Entity<AppOtpChallenge>()
            .HasQueryFilter(x => !Tenant.HasValue || x.MerchantId == Tenant.Value);

        builder.Entity<AppToken>()
            .HasQueryFilter(x => !Tenant.HasValue || x.MerchantId == Tenant.Value);

        builder.Entity<AppDevicePairingToken>()
            .HasQueryFilter(x => !Tenant.HasValue || x.MerchantId == Tenant.Value);

        builder.Entity<AppDevice>()
            .HasQueryFilter(x => !Tenant.HasValue || x.MerchantId == Tenant.Value);

        builder.Entity<WorkflowSession>()
            .HasQueryFilter(x => !Tenant.HasValue || x.MerchantId == Tenant.Value);

        builder.Entity<WorkflowAction>()
            .HasQueryFilter(x => !Tenant.HasValue || x.MerchantId == Tenant.Value);

        // snake_case, because every other tool that will ever touch this database - psql, a
        // backup, somebody's ad-hoc query at two in the morning - is easier to read that
        // way, and because unquoted mixed case in Postgres is a trap that only shows up in
        // hand-written SQL.
        foreach (var entity in builder.Model.GetEntityTypes().ToList())
        {
            entity.SetTableName(SnakeCase(entity.GetTableName()!));

            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(SnakeCase(property.GetColumnName()));
            }

            foreach (var key in entity.GetKeys()) key.SetName(SnakeCase(key.GetName()!));

            foreach (var index in entity.GetIndexes())
            {
                index.SetDatabaseName(SnakeCase(index.GetDatabaseName()!));
            }
        }
    }

    /// <summary>
    /// e =&gt; !e.IsDeleted &amp;&amp; (Tenant == null || e.TenantId == Tenant)
    ///
    /// Built as an expression tree because the filter has to be attached per entity type
    /// and C# generics cannot say "whatever this runtime type is".
    /// </summary>
    private LambdaExpression BuildFilter(Type clr)
    {
        var parameter = Expression.Parameter(clr, "e");

        var notDeleted = Expression.Not(
            Expression.Property(parameter, nameof(BaseEntity.IsDeleted)));

        // Captured as a member access on this context rather than as a constant: EF
        // compiles the filter once and re-reads this per query, which is what makes the
        // value current rather than whatever it happened to be at start-up.
        var tenantProperty = Expression.Property(Expression.Constant(this), nameof(Tenant));

        var hasTenant = Expression.Property(tenantProperty, nameof(Nullable<Guid>.HasValue));
        var tenantValue = Expression.Property(tenantProperty, nameof(Nullable<Guid>.Value));

        var sameMerchant = Expression.Equal(
            Expression.Property(parameter, nameof(BaseEntity.TenantId)), tenantValue);

        var tenantOk = Expression.OrElse(Expression.Not(hasTenant), sameMerchant);

        return Expression.Lambda(Expression.AndAlso(notDeleted, tenantOk), parameter);
    }

    /// <summary>
    /// Read by the query filter through an expression tree, so it cannot be private: the
    /// filter is compiled by EF, not invoked by this class.
    /// </summary>
    public Guid? Tenant => tenant.Current;

    public override int SaveChanges()
    {
        Stamp();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Stamp();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// The audit trail and the concurrency token, written here rather than by each caller,
    /// because the one caller who forgets is the one that matters.
    ///
    /// The bump happens before EF builds the statement, so the update reads
    /// SET row_version = new WHERE row_version = old. Anything that reaches the database
    /// through raw SQL is outside this and has to bump it itself.
    /// </summary>
    private void Stamp()
    {
        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            if (entry.State != EntityState.Modified) continue;

            entry.Entity.UpdatedAt = DateTimeOffset.UtcNow;
            entry.Entity.RowVersion++;
        }
    }

    private static string SnakeCase(string name) =>
        Regex.Replace(name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
}
