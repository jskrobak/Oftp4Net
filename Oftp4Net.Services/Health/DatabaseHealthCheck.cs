using Havit.Data.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Oftp4Net.Services.Health;

/// <summary>
/// The database can be reached and its schema is the one the application expects. Its size and the largest
/// tables are in the data, so that the monitoring sees it grow.
/// </summary>
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

        return HealthCheckResult.Healthy("The database is reachable and up to date.", await SizesAsync(database, cancellationToken));
    }

    private sealed class TableSize
    {
        public string Name { get; set; } = "";
        public long Size { get; set; }
    }

    private static async Task<Dictionary<string, object>> SizesAsync(DatabaseFacade database, CancellationToken cancellationToken)
    {
        var data = new Dictionary<string, object>
        {
            ["sizeBytes"] = await database.SqlQueryRaw<long>("SELECT pg_database_size(current_database()) AS \"Value\"")
                .SingleAsync(cancellationToken),
        };

        var tables = await database.SqlQueryRaw<TableSize>(
                "SELECT relname AS \"Name\", pg_total_relation_size(relid) AS \"Size\" FROM pg_catalog.pg_statio_user_tables " +
                "ORDER BY 2 DESC LIMIT 5")
            .ToListAsync(cancellationToken);
        foreach (var table in tables)
            data[$"{table.Name}Bytes"] = table.Size;

        return data;
    }
}
