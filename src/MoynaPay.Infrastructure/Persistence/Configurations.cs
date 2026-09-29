using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MoynaPay.Application.Abstractions;
using MoynaPay.Domain.Merchants;
using MoynaPay.Domain.Orders;
using MoynaPay.Domain.Payments;

namespace MoynaPay.Infrastructure.Persistence;

/// <summary>
/// What each table needs beyond the conventions: which schema it belongs to, how long its
/// strings are, and - the part that earns its keep - which indexes exist.
///
/// Enums, money, timestamps and JSON columns are not here. They are applied to every
/// entity by the reflection pass in MoynaPayDbContext, because a rule written once holds
/// for the entity somebody adds next year and a rule written per table does not.
/// </summary>
internal static class Schemas
{
    public const string Merchants = "merchants";
    public const string Orders = "orders";
    public const string Payments = "payments";
}

internal sealed class MerchantConfiguration : IEntityTypeConfiguration<Merchant>
{
    public void Configure(EntityTypeBuilder<Merchant> builder)
    {
        builder.Metadata.SetSchema(Schemas.Merchants);

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Msisdn).HasMaxLength(20).IsRequired();
        builder.Property(x => x.TimeZone).HasMaxLength(60).IsRequired();
        builder.Property(x => x.Address).HasMaxLength(500);
        builder.Property(x => x.SupportMsisdn).HasMaxLength(20);

        // The owner's phone is the app login identity, not a detail on the record. Two
        // rows with the same number and the lookup picks whichever the index reaches
        // first, which is how one merchant ends up inside another's account.
        builder.HasIndex(x => x.Msisdn).IsUnique().HasFilter("is_deleted = false");
    }
}

internal sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> builder)
    {
        builder.Metadata.SetSchema(Schemas.Merchants);

        builder.Property(x => x.Plan).HasMaxLength(40).IsRequired();

        // One row per merchant. Two would mean the pipeline's shape depends on which one a
        // query happened to read first.
        builder.HasIndex(x => x.TenantId).IsUnique().HasFilter("is_deleted = false");
    }
}

internal sealed class ApiCredentialConfiguration : IEntityTypeConfiguration<ApiCredential>
{
    public void Configure(EntityTypeBuilder<ApiCredential> builder)
    {
        builder.Metadata.SetSchema(Schemas.Merchants);

        builder.Property(x => x.KeyId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.SecretCipher).HasMaxLength(512).IsRequired();
        builder.Property(x => x.KeyRingId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Label).HasMaxLength(120).IsRequired();

        // Looked up on every signed request, before the merchant is known - so this index
        // is global, not per merchant.
        builder.HasIndex(x => x.KeyId).IsUnique();

        builder.Ignore(x => x.IsActive);
    }
}

internal sealed class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpoint>
{
    public void Configure(EntityTypeBuilder<WebhookEndpoint> builder)
    {
        builder.Metadata.SetSchema(Schemas.Merchants);

        builder.Property(x => x.Url).HasMaxLength(500).IsRequired();
        builder.Property(x => x.SecretCipher).HasMaxLength(512).IsRequired();
        builder.Property(x => x.KeyRingId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.LastFailureReason).HasMaxLength(500);

        builder.HasIndex(x => x.TenantId);
    }
}

internal sealed class RequestNonceConfiguration : IEntityTypeConfiguration<RequestNonce>
{
    public void Configure(EntityTypeBuilder<RequestNonce> builder)
    {
        // The schema is named on ToTable, not only on SetSchema, because ToTable(name) on
        // its own does not leave the schema alone - it resets it to the default. Without
        // the second argument the table is created somewhere the store does not look, and
        // that failure appears as every signed request suddenly being refused.
        builder.ToTable("request_nonces", Schemas.Merchants);

        builder.Property(x => x.KeyId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Nonce).HasMaxLength(120).IsRequired();

        // The index that makes a signature usable exactly once.
        builder.HasIndex(x => new { x.KeyId, x.Nonce }).IsUnique();

        // Swept by age. Nothing older than the tolerance can be replayed anyway.
        builder.HasIndex(x => x.ExpiresAt);
    }
}

internal sealed class RateLimitHitConfiguration : IEntityTypeConfiguration<RateLimitHit>
{
    public void Configure(EntityTypeBuilder<RateLimitHit> builder)
    {
        builder.ToTable("rate_limit_hits", Schemas.Merchants);

        builder.Property(x => x.Key).HasMaxLength(200).IsRequired();

        // Both halves of the only question ever asked of this table: how many hits for
        // this key since a moment. Also what the sweep deletes by.
        builder.HasIndex(x => new { x.Key, x.At });
    }
}

/// <summary>
/// The OTP challenge and the app token live in the Application assembly rather than the
/// Domain one, because that is where phase 12 put them.
///
/// They are mapped where they are. Moving them would touch every file phases 12 to 17
/// wrote, to gain tidiness and nothing else - and a large rename is exactly the change
/// that hides a real one.
///
/// They are not BaseEntity, so the reflection pass does not reach them: their tenant
/// filter is written out here by hand, and they have no soft delete and no row version.
/// Neither is a loss. Nothing ever updates an OTP row except to consume it, and nothing
/// deletes one - they expire.
/// </summary>
internal sealed class AppOtpChallengeConfiguration : IEntityTypeConfiguration<AppOtpChallenge>
{
    public void Configure(EntityTypeBuilder<AppOtpChallenge> builder)
    {
        builder.ToTable("app_otp_challenges", Schemas.Merchants);

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Msisdn).HasMaxLength(20).IsRequired();

        // Only the hash is stored. A code that can be read out of the database is a code
        // that whoever reads the database can use.
        builder.Property(x => x.CodeHash).HasMaxLength(128).IsRequired();

        // "The latest challenge for this number", which is the only way it is ever read.
        builder.HasIndex(x => new { x.Msisdn, x.CreatedAt });
    }
}

internal sealed class AppTokenConfiguration : IEntityTypeConfiguration<AppToken>
{
    public void Configure(EntityTypeBuilder<AppToken> builder)
    {
        builder.ToTable("app_tokens", Schemas.Merchants);

        builder.HasKey(x => x.Id);

        builder.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ReplacedByHash).HasMaxLength(128);

        // A token is presented, not looked up by id, so this is the real primary key as
        // far as reads are concerned. Unique per kind: an access token and a refresh token
        // are different rows and must not collide.
        builder.HasIndex(x => new { x.TokenHash, x.Kind }).IsUnique();

        // What logout and rotation need: every live token for one merchant.
        builder.HasIndex(x => new { x.MerchantId, x.ExpiresAt });
    }
}

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.Metadata.SetSchema(Schemas.Orders);

        builder.Property(x => x.Reference).HasMaxLength(80).IsRequired();
        builder.Property(x => x.CustomerName).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Msisdn).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Address).HasMaxLength(500);
        builder.Property(x => x.Summary).HasMaxLength(500);
        builder.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        builder.Property(x => x.Digit).HasMaxLength(4);
        builder.Property(x => x.Reason).HasMaxLength(500);
        builder.Property(x => x.TrxId).HasMaxLength(64);
        builder.Property(x => x.Courier).HasMaxLength(40);
        builder.Property(x => x.TrackingCode).HasMaxLength(80);
        builder.Property(x => x.CallbackUrl).HasMaxLength(500);
        builder.Property(x => x.ClaimedBy).HasMaxLength(120);

        builder.Ignore(x => x.MerchantId);

        // The index that makes POST /v1/orders idempotent under load.
        //
        // MemoryOrderStore checks for a clash in code; that is a check, not a guarantee -
        // two requests arriving at once can both pass it. This is the guarantee. Filtered
        // on is_deleted so a merchant can reuse a reference after removing an order.
        builder.HasIndex(x => new { x.TenantId, x.Reference })
            .IsUnique()
            .HasFilter("is_deleted = false");

        // The list screen and the review queue: what is open, oldest first.
        builder.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt });
    }
}

internal sealed class OrderEventConfiguration : IEntityTypeConfiguration<OrderEvent>
{
    public void Configure(EntityTypeBuilder<OrderEvent> builder)
    {
        builder.Metadata.SetSchema(Schemas.Orders);

        builder.Property(x => x.Type).HasMaxLength(60).IsRequired();
        builder.Property(x => x.ActorName).HasMaxLength(120);
        builder.Property(x => x.Detail).HasMaxLength(1000);

        // The timeline, in order, for one order. Read on every order detail screen.
        builder.HasIndex(x => new { x.OrderId, x.At });
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.Metadata.SetSchema(Schemas.Orders);

        builder.Property(x => x.EventType).HasMaxLength(60).IsRequired();
        builder.Property(x => x.PayloadJson).IsRequired();
        builder.Property(x => x.LastFailureReason).HasMaxLength(500);

        // What the dispatcher asks for, every few seconds, forever: undelivered and due.
        // Partial so the index stays small - delivered rows are the overwhelming majority
        // and this query never reads one of them again.
        builder.HasIndex(x => x.NextAttemptAt).HasFilter("delivered_at is null");

        // "Is there an older undelivered message for this order?", asked once per candidate
        // row by the claim query in phase 3.
        builder.HasIndex(x => new { x.OrderId, x.CreatedAt })
            .HasFilter("delivered_at is null");
    }
}

/// <summary>
/// A workflow session is identified by what it is about, not by an id of its own:
/// (merchant, order, workflow name). That composite key is the idempotency - a second
/// attempt to start the same workflow for the same order finds the row that exists rather
/// than making a second one.
/// </summary>
internal sealed class WorkflowSessionConfiguration : IEntityTypeConfiguration<WorkflowSession>
{
    public void Configure(EntityTypeBuilder<WorkflowSession> builder)
    {
        builder.ToTable("workflow_sessions", Schemas.Orders);

        builder.HasKey(x => new { x.MerchantId, x.OrderId, x.Name });

        builder.Property(x => x.Name).HasMaxLength(60);
        builder.Property(x => x.Step).HasMaxLength(60).IsRequired();
        builder.Property(x => x.LeaseOwner).HasMaxLength(120);

        // The history is a list of step names, and it is read whole or not at all - never
        // filtered, never joined. A jsonb array is the honest shape for that; a child table
        // would cost a join on every read to buy ordering nothing asks for.
        //
        // The comparer is not optional. Without it EF compares the List by reference, so a
        // step appended to the existing instance looks unchanged and is never saved.
        builder.Property(x => x.History)
            .HasColumnType("jsonb")
            .HasConversion(
                list => JsonSerializer.Serialize(list, (JsonSerializerOptions?)null),
                text => JsonSerializer.Deserialize<List<string>>(text, (JsonSerializerOptions?)null)
                    ?? new List<string>(),
                new ValueComparer<List<string>>(
                    (a, b) => a != null && b != null && a.SequenceEqual(b),
                    v => v.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode(StringComparison.Ordinal))),
                    v => v.ToList()))
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // What the runner leases: an unfinished session of this workflow that nobody holds.
        builder.HasIndex(x => new { x.Name, x.Complete, x.LeaseUntil });
    }
}

internal sealed class WorkflowActionConfiguration : IEntityTypeConfiguration<WorkflowAction>
{
    public void Configure(EntityTypeBuilder<WorkflowAction> builder)
    {
        builder.ToTable("workflow_actions", Schemas.Orders);

        // The whole point of this table. "Book the courier for this order" is one row, so
        // running the handler twice writes the same row twice and has the effect of once.
        builder.HasKey(x => new { x.MerchantId, x.OrderId, x.ActionId });

        builder.Property(x => x.ActionId).HasMaxLength(80);
        builder.Property(x => x.Result).HasMaxLength(500);
    }
}

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.Metadata.SetSchema(Schemas.Payments);

        builder.Property(x => x.OrderRef).HasMaxLength(80).IsRequired();
        builder.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        builder.Property(x => x.CustomerName).HasMaxLength(120);
        builder.Property(x => x.CustomerEmail).HasMaxLength(200);
        builder.Property(x => x.CustomerMsisdn).HasMaxLength(20);
        builder.Property(x => x.RedirectUrl).HasMaxLength(500);
        builder.Property(x => x.CallbackUrl).HasMaxLength(500);

        // The rest of the payment module - wallets, sessions, claims, matches, devices,
        // parser templates - is not mapped yet, so these navigations have nowhere to go.
        // They come back with the matcher, in phases 32 to 35, along with their tables.
        builder.Ignore(x => x.Wallet);
        builder.Ignore(x => x.Sessions);

        builder.Ignore(x => x.MerchantId);

        // One invoice per order reference per merchant, which is what makes creating an
        // invoice safe to retry.
        builder.HasIndex(x => new { x.TenantId, x.OrderRef })
            .IsUnique()
            .HasFilter("is_deleted = false");
    }
}
