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

    public static IServiceCollection AddMoynaPay(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IClock, SystemClock>();
        services.AddHttpClient<IWebhookSender, HttpWebhookSender>();

        if (IsInMemory(configuration))
        {
            // Development only. Phase 2 puts the EF Core stores on the other side of this
            // branch - same ports, same registrations, different rows.
            services.AddSingleton<MemoryDatabase>();
            services.AddSingleton<IMerchantStore, MemoryMerchantStore>();
            services.AddSingleton<IOrderStore, MemoryOrderStore>();
            services.AddSingleton<IInvoiceStore, MemoryInvoiceStore>();
            services.AddSingleton<IWorkflowSessionStore, MemoryWorkflowSessionStore>();
            services.AddSingleton<IWorkflowActionStore, MemoryWorkflowActionStore>();
            services.AddSingleton<INonceStore, MemoryNonceStore>();
            services.AddSingleton<ISecretProtector>(_ =>
                AesGcmSecretProtector.FromConfiguration(configuration));

            return services;
        }

        throw new InvalidOperationException(
            "A Postgres connection string is configured, but the database stores arrive in " +
            "phase 2. Remove the connection string to run on the in-memory fallback.");
    }
}
