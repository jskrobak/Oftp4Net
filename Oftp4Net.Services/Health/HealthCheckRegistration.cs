using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Oftp4Net.Services.Health;

public static class HealthCheckRegistration
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Registers the health checks of the server and the <see cref="HealthMonitor"/> that runs them regularly.
    /// The checks only read the state the services keep and the database; none of them connects to a partner or
    /// downloads anything.
    /// </summary>
    public static IServiceCollection AddOftpHealthChecks(this IServiceCollection services)
    {
        services.AddSingleton<SendQueueReportService>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<IHealthCheckPublisher>(sp => sp.GetRequiredService<HealthMonitor>());
        services.Configure<HealthCheckPublisherOptions>(options =>
        {
            options.Delay = TimeSpan.FromSeconds(5);
            options.Period = TimeSpan.FromSeconds(30);
        });

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: [HealthTags.Ready], timeout: Timeout)
            .AddCheck<StorageHealthCheck>("storage", tags: [HealthTags.Ready], timeout: Timeout)
            .AddCheck<ListenersHealthCheck>("listeners", tags: [HealthTags.Ready], timeout: Timeout)
            .AddCheck<SendServiceHealthCheck>("send-service", tags: [HealthTags.Ready], timeout: Timeout)
            // Operational checks report what needs attention, but never make the server unhealthy.
            .AddCheck<TrustListHealthCheck>("trust-list", HealthStatus.Degraded, [HealthTags.Operational], Timeout)
            .AddCheck<RevocationListsHealthCheck>("revocation-lists", HealthStatus.Degraded, [HealthTags.Operational], Timeout)
            .AddCheck<CertificatesHealthCheck>("certificates", HealthStatus.Degraded, [HealthTags.Operational], Timeout)
            .AddCheck<SendQueueHealthCheck>("send-queue", HealthStatus.Degraded, [HealthTags.Operational], Timeout)
            .AddCheck<InternalQueuesHealthCheck>("internal-queues", HealthStatus.Degraded, [HealthTags.Operational], Timeout)
            .AddCheck<RetentionHealthCheck>("retention", HealthStatus.Degraded, [HealthTags.Operational], Timeout);

        return services;
    }
}
