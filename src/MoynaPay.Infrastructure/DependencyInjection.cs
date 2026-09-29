using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MoynaPay.Application.Abstractions;
using MoynaPay.Infrastructure.Memory;
using MoynaPay.Infrastructure.Security;
using MoynaPay.Infrastructure.Webhooks;

namespace MoynaPay.Infrastructure;

/// <summary>
/// Everything that touches the outside world is registered here, in one place.
///
/// The hosts - the API and the three workers - do not choose a store, a clock or a
/// protector. They ask for MoynaPay, and what they get depends on configuration. That is
/// what lets the same four processes run against an in-memory store on a laptop and
/// Postgres on a server without a line of difference in them.
/// </summary>
public static class DependencyInjection
{
    public const string ConnectionName = "Postgres";

    /// <summary>
    /// True when no database is configured, so the service is running on the development
    /// fallback. Hosts say so out loud at start-up: a service holding a merchant's orders
    /// in memory looks healthy right up until it restarts.
    /// </summary>
    public static bool IsInMemory(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return string.IsNullOrWhiteSpace(configuration.GetConnectionString(ConnectionName));
    }

    /// <param name="isDevelopment">
    /// Hosts pass their own environment. Two things here are safe on a laptop and are not
    /// safe anywhere else - the published fallback key ring, and the in-memory stores -
    /// and neither can decide that for itself from configuration alone.
    /// </param>
    public static IServiceCollection AddMoynaPay(
        this IServiceCollection services, IConfiguration configuration, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IClock, SystemClock>();

        // Redirects off and a connect-time address check, because a string check on the
        // url is not enough on its own: a host name is resolved when the connection is
        // made, so "webhook.theirshop.com" pointing at 10.0.0.5 passes every check that
        // reads text. See WebhookGuard.
        services.AddHttpClient<IWebhookSender, HttpWebhookSender>()
            .ConfigurePrimaryHttpMessageHandler(WebhookGuard.Handler);

        if (IsInMemory(configuration))
        {
            // Development only. The EF Core stores go on the other side of this branch in
            // A2 - same ports, same registrations, different rows.
            services.AddScoped<ITenantContext, TenantContext>();

            services.AddSingleton<MemoryDatabase>();
            services.AddSingleton<IMerchantStore, MemoryMerchantStore>();
            services.AddSingleton<IOrderStore, MemoryOrderStore>();
            services.AddSingleton<IInvoiceStore, MemoryInvoiceStore>();
            services.AddSingleton<IWorkflowSessionStore, MemoryWorkflowSessionStore>();
            services.AddSingleton<IWorkflowActionStore, MemoryWorkflowActionStore>();
            services.AddSingleton<IAppAuthStore, MemoryAppAuthStore>();
            services.AddSingleton<IRateLimitStore, MemoryRateLimitStore>();
            services.AddSingleton<INonceStore, MemoryNonceStore>();
            services.AddSingleton<ISecretProtector>(_ =>
                AesGcmSecretProtector.FromConfiguration(configuration, isDevelopment));

            return services;
        }

        // A1 maps the schema and generates the migration. The stores that read and write
        // through it arrive in A2, so a connection string is not yet enough to run on.
        // Refusing is the honest answer: starting and quietly serving from memory while a
        // database sits there configured is how somebody spends an afternoon wondering why
        // their tables are empty.
        throw new InvalidOperationException(
            "A Postgres connection string is configured, but the EF stores arrive in A2. " +
            "The schema and migration exist - run `dotnet ef database update` to create " +
            "the tables - but nothing reads them yet. Remove the connection string to run " +
            "on the in-memory fallback.");
    }
}
