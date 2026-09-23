using System.Threading.Channels;
using Havit.Data.Patterns.UnitOfWorks;
using Havit.Services.TimeServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.Services.Health;

namespace Oftp4Net.Services.TransferEvents;

public interface ITransferEventLog
{
    /// <summary>Queues the record for storing. Returns immediately, never throws.</summary>
    void Record(TransferEvent transferEvent);
}

/// <summary>
/// Stores <see cref="TransferEvent"/> records in batches in the background (so recording never slows down an OFTP
/// session) and once a day marks records older than <see cref="GlobalSettings.ArchiveEventsAfterDays"/> as archived.
/// </summary>
public sealed class TransferEventLog(
    IServiceScopeFactory serviceScopeFactory,
    GlobalSettingsService settingsService,
    ITimeService timeService,
    ILogger<TransferEventLog> logger) : BackgroundService, ITransferEventLog
{
    private const int MaxBatchSize = 200;
    private static readonly TimeSpan ArchiveInterval = TimeSpan.FromHours(24);

    private readonly MonitoredQueue<TransferEvent> _queue = new("Transfer log", 10_000, BoundedChannelFullMode.DropOldest,
        dropped => logger.LogError("The transfer log cannot keep up, {Count} record(s) lost so far", dropped));

    /// <summary>Records waiting to be stored.</summary>
    public QueueState Queue => _queue.State;

    private DateTime _nextArchive = DateTime.MinValue;

    public void Record(TransferEvent transferEvent)
    {
        transferEvent.Message = Truncate(transferEvent.Message, 1000) ?? "";
        transferEvent.PartnerName = Truncate(transferEvent.PartnerName, 50);
        transferEvent.RemoteEndPoint = Truncate(transferEvent.RemoteEndPoint, 100);
        transferEvent.Details = Truncate(transferEvent.Details, 20_000);
        _queue.Writer.TryWrite(transferEvent);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<TransferEvent>();
        while (true)
        {
            try
            {
                // Wake up at least every hour to archive, even when nothing happens.
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(TimeSpan.FromHours(1));
                try
                {
                    await _queue.Reader.WaitToReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                }

                while (batch.Count < MaxBatchSize && _queue.Reader.TryRead(out var item))
                    batch.Add(item);

                if (batch.Count > 0)
                    await StoreAsync(batch, CancellationToken.None);
                batch.Clear();

                if (stoppingToken.IsCancellationRequested)
                    break;

                await ArchiveIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Storing transfer events failed, {Count} record(s) lost", batch.Count);
                batch.Clear();
            }
        }

        // Store what is left when the application stops.
        while (_queue.Reader.TryRead(out var item))
            batch.Add(item);
        if (batch.Count > 0)
            await StoreAsync(batch, CancellationToken.None);
    }

    private async Task StoreAsync(List<TransferEvent> batch, CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        unitOfWork.AddRangeForInsert(batch);
        await unitOfWork.CommitAsync(cancellationToken);
    }

    private async Task ArchiveIfDueAsync(CancellationToken cancellationToken)
    {
        var now = timeService.GetCurrentTime();
        if (now < _nextArchive)
            return;
        _nextArchive = now + ArchiveInterval;

        var days = (await settingsService.GetGlobalSettingsAsync()).ArchiveEventsAfterDays;
        using var scope = serviceScopeFactory.CreateScope();
        var archived = await scope.ServiceProvider.GetRequiredService<ITransferEventRepository>()
            .ArchiveOlderThanAsync(now.AddDays(-days), cancellationToken);

        if (archived > 0)
            logger.LogInformation("Archived {Count} transfer event(s) older than {Days} days", archived, days);
    }

    private static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];
}
