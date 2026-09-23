namespace Oftp4Net.Services.Health;

/// <summary>Groups of health checks, each answered by its own endpoint.</summary>
public static class HealthTags
{
    /// <summary>
    /// Without it the server cannot do its work (database, storage, listeners, send service); a failure is
    /// <c>Unhealthy</c> and <c>/health/ready</c> answers 503.
    /// </summary>
    public const string Ready = "ready";

    /// <summary>
    /// Things an administrator has to look at (certificates, revocation lists, stuck files); at worst
    /// <c>Degraded</c>, so that nobody restarts the server because of them.
    /// </summary>
    public const string Operational = "operational";
}
