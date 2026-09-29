using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MoynaPay.Infrastructure.Persistence;

public static class PersistenceSetup
{
    /// <summary>
    /// One place where the provider is configured, used by the running service and by
    /// `dotnet ef` alike.
    ///
    /// Two places is how the migrations history table ends up in one schema while the
    /// service looks for it in another, finds nothing, and concludes that no migration has
    /// ever run.
    /// </summary>
    public static void UseMoynaPay(this DbContextOptionsBuilder options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.MigrationsHistoryTable("__migrations", "moynapay");

            // Transient faults happen: a failover, a restart, a network blip. What this
            // must NOT do is hide a user-initiated transaction - the retrying strategy
            // forbids those, and every place that needs one wraps the work in
            // CreateExecutionStrategy().ExecuteAsync instead.
            npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), null);
        });
    }
}

/// <summary>
/// Used only by `dotnet ef`, which has to build a context without starting the app.
///
/// The connection string comes from the environment rather than from a default that looks
/// plausible. A default that lies costs more than it saves: the error it produces says
/// "28P01 password authentication failed" and gives no hint of where the password came
/// from.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MoynaPayDbContext>
{
    public MoynaPayDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? throw new InvalidOperationException(
                "Set ConnectionStrings__Postgres before running dotnet ef. There is no " +
                "default: a wrong one that looks right is worse than none.");

        var options = new DbContextOptionsBuilder<MoynaPayDbContext>();
        options.UseMoynaPay(connectionString);

        // No merchant in particular, which is what a migration wants: the filters are
        // compiled into the model, not into the schema, so nothing here is scoped.
        return new MoynaPayDbContext(options.Options, new TenantContext());
    }
}
