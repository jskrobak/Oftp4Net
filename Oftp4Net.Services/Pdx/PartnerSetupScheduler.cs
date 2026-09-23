using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oftp4Net.DataLayer.Repositories;

namespace Oftp4Net.Services.Pdx;

/// <summary>
/// Applies scheduled OFTP2 Communication Setups at their <c>validfrom</c> time: it sleeps until the next one is due,
/// so that e.g. a new certificate of a partner is used from that moment on (Odette OP09 PDX test 4.1).
/// </summary>
public class PartnerSetupScheduler(IServiceScopeFactory serviceScopeFactory, ILogger<PartnerSetupScheduler> logger) : BackgroundService
{
    /// <summary>Checked at least this often, e.g. after the clock was changed.</summary>
    private static readonly TimeSpan MaxSleep = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _trigger = new(0, 1);

    /// <summary>A datasheet was scheduled: the next due time may be earlier than the one waited for.</summary>
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
                await scope.ServiceProvider.GetRequiredService<PartnerSetupService>().ActivateDueAsync(stoppingToken);

                if (await scope.ServiceProvider.GetRequiredService<IPartnerSetupDocumentRepository>().GetNextScheduledAsync(stoppingToken) is { } next)
                    sleep = Clamp(next - DateTime.Now);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Applying scheduled OFTP2 Communication Setups failed");
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
