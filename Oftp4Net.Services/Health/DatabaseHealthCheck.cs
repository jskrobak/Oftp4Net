using Havit.Data.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Oftp4Net.Services.Health;

/// <summary>The database can be reached and its schema is the one the application expects.</summary>
public sealed class DatabaseHealthCheck(IServiceScopeFactory serviceScopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var database = ((Microsoft.EntityFrameworkCore.DbContext)scope.ServiceProvider.GetRequiredService<IDbContext>()).Database;

        if (!await database.CanConnectAsync(cancellationToken))
            return HealthCheckResult.Unhealthy("The database cannot be reached.");

        // Only possible with Database:MigrateOnStartup switched off, otherwise the application would not have started.
        var pending = (await database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
            return HealthCheckResult.Unhealthy(
                $"{pending.Count} migration(s) of the database schema are not applied: {string.Join(", ", pending)}.");

        return HealthCheckResult.Healthy("The database is reachable and up to date.");
    }
}
