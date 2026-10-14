using Havit.Services.TimeServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oftp4Net.DataLayer.Repositories;

namespace Oftp4Net.Services.Security;

/// <summary>
/// Applies scheduled certificate changes of partners at their time: it sleeps until the next one is due, so that a
/// new certificate is used from that moment on.
/// </summary>
public class CertificateChangeScheduler(IServiceScopeFactory serviceScopeFactory, ITimeService timeService,
    ILogger<CertificateChangeScheduler> logger) : BackgroundService
{
    /// <summary>Checked at least this often, e.g. after the clock was changed.</summary>
    private static readonly TimeSpan MaxSleep = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _trigger = new(0, 1);

    /// <summary>When the scheduler last looked for due changes.</summary>
    public DateTime? LastRun { get; private set; }

    /// <summary>A change was scheduled: the next due time may be earlier than the one waited for.</summary>
    public void Trigger()
    {
        try
        {
            _trigger.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already triggered.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var sleep = MaxSleep;
            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<CertificateChangeService>().ApplyDueAsync(stoppingToken);
                LastRun = timeService.GetCurrentTime();

                if (await scope.ServiceProvider.GetRequiredService<ICertificateChangeRepository>().GetNextScheduledAsync(stoppingToken) is { } next)
                    // The times are in the time of the application, not of the machine.
                    sleep = Clamp(next - timeService.GetCurrentTime());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Applying scheduled certificate changes failed");
            }

            try
            {
                await _trigger.WaitAsync(sleep, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static TimeSpan Clamp(TimeSpan delay) =>
        delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay > MaxSleep ? MaxSleep : delay;
}
