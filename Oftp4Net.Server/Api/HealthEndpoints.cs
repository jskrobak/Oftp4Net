using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Oftp4Net.Services.Health;

namespace Oftp4Net.Server.Api;

/// <summary>
/// Health endpoints for container orchestration and monitoring:
/// <c>/health/live</c> (the process answers), <c>/health/ready</c> (the server can work, 503 when not) and
/// <c>/health/details</c> (all checks as JSON, with an API token).
/// </summary>
public static class HealthEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static void MapHealth(this WebApplication app)
    {
        // No check at all: a database outage must not make the orchestrator restart the server again and again.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
            .AllowAnonymous();

        // Only the state, the details name listeners, partners and certificates.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(HealthTags.Ready) })
            .AllowAnonymous();

        app.MapHealthChecks("/health/details", new HealthCheckOptions { ResponseWriter = WriteDetailsAsync })
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = ApiTokenAuthenticationHandler.SchemeName });
    }

    private static Task WriteDetailsAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            duration = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.ToDictionary(e => e.Key, e => new
            {
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                duration = e.Value.Duration.TotalMilliseconds,
                tags = e.Value.Tags,
                data = e.Value.Data.Count == 0 ? null : e.Value.Data,
                error = e.Value.Exception?.Message,
            }),
        }, JsonOptions));
    }
}
