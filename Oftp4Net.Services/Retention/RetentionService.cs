using System.Text.Json;
using Havit.Data.Patterns.UnitOfWorks;
using Havit.Services.TimeServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;

namespace Oftp4Net.Services.Retention;

/// <summary>When the data of each kind becomes old enough to be removed.</summary>
public sealed record RetentionCutoffs(DateTime Content, DateTime InformationEvents, DateTime Events, DateTime FinishedFiles)
{
    /// <summary>Records received again within this time are recognised as duplicates by their record.</summary>
    public const int MinFinishedFilesDays = 30;

    /// <summary>
    /// The periods of the settings, each at least as long as the one before, so that nothing is deleted before its
    /// details went to the archive.
    /// </summary>
    public static RetentionCutoffs From(GlobalSettings settings, DateTime now)
    {
        var content = settings.DeleteContentAfterDays;
        var information = Math.Max(settings.DeleteInformationEventsAfterDays, content);
        var events = Math.Max(settings.DeleteEventsAfterDays, information);
        var finished = Math.Max(settings.DeleteFinishedFilesAfterDays, MinFinishedFilesDays);
        return new RetentionCutoffs(now.AddDays(-content), now.AddDays(-information), now.AddDays(-events), now.AddDays(-finished));
    }
}

/// <summary>What one run removed.</summary>
public sealed record RetentionResult(DateTime Started, DateTime Finished, int EventsArchived, int OutboxFilesDeleted,
    int EventsDeleted, int SendQueueItemsDeleted, int ReceivedFilesDeleted, string? Error)
{
    public override string ToString() =>
        $"{EventsArchived} transfer log record(s) archived, {EventsDeleted} deleted, {OutboxFilesDeleted} outbox file(s) deleted, " +
        $"{SendQueueItemsDeleted} send queue item(s) and {ReceivedFilesDeleted} received file record(s) archived and deleted";
}

/// <summary>
/// Keeps the database within bounds without losing anything that matters. Once a night, in this order:
/// <list type="number">
/// <item>Transfer log records older than <see cref="GlobalSettings.DeleteContentAfterDays"/> are written to the
/// archive in full and their details and hook parameters are removed (a hook run cannot be repeated then); the files of send queue items delivered before that time are
/// deleted from the outbox.</item>
/// <item>Transfer log records are deleted, the informational ones after
/// <see cref="GlobalSettings.DeleteInformationEventsAfterDays"/>, the others after
/// <see cref="GlobalSettings.DeleteEventsAfterDays"/>; they are in the archive already.</item>
/// <item>Send queue items and received files that need nothing more are written to the archive and deleted after
/// <see cref="GlobalSettings.DeleteFinishedFilesAfterDays"/>.</item>
/// </list>
/// Nothing unfinished is touched: files waiting, failed or waiting for their End to End Response, in either
/// direction. The received files themselves are left where they are, they belong to the integration. Every
/// record goes to the archive before it is removed; after a crash in between it may be there twice.
/// </summary>
public sealed class RetentionService(
    IServiceScopeFactory serviceScopeFactory,
    GlobalSettingsService settingsService,
    ITimeService timeService,
    ILogger<RetentionService> logger) : BackgroundService
{
    /// <summary>Settings item that remembers up to which transfer log record the archive is written.</summary>
    public const string EventsExportedUntilKey = "RetentionEventsExportedUntil";

    public const string EventsKind = "transfer-log";
    public const string SendQueueKind = "send-queue";
    public const string ReceivedFilesKind = "received-files";

    private const int BatchSize = 2000;
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly SemaphoreSlim _trigger = new(0, 1);
    private readonly SemaphoreSlim _running = new(1, 1);

    public DateTime? Started { get; private set; }
    public RetentionResult? LastResult { get; private set; }
    public DateTime? LastSuccess { get; private set; }
    public bool IsRunning => _running.CurrentCount == 0;

    /// <summary>Raised when a run starts or ends.</summary>
    public event Action? Changed;

    /// <summary>Runs now instead of waiting for the next night.</summary>
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
        Started = timeService.GetCurrentTime();
        var delay = FirstRunDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _trigger.WaitAsync(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = Interval;
            if ((await settingsService.GetGlobalSettingsAsync()).RetentionEnabled)
                await RunAsync(stoppingToken);
        }
    }

    public async Task<RetentionResult> RunAsync(CancellationToken cancellationToken = default)
    {
        await _running.WaitAsync(cancellationToken);
        Changed?.Invoke();
        var started = timeService.GetCurrentTime();
        int archived = 0, outbox = 0, deleted = 0, sendItems = 0, receivedFiles = 0;
        var errors = new List<string>();
        RetentionResult result;
        try
        {
            var settings = await settingsService.GetGlobalSettingsAsync();
            var cutoffs = RetentionCutoffs.From(settings, started);
            var archive = settingsService.ResolvePath(settings.RetentionArchiveDirectory);

            // Every step on its own, so that one failing does not keep the others from keeping the database small.
            archived = await StepAsync("archiving the transfer log", () => ArchiveEventsAsync(cutoffs.Content, archive, cancellationToken), errors);
            outbox = await StepAsync("deleting outbox files", () => DeleteOutboxFilesAsync(settings, cutoffs.Content, cancellationToken), errors);
            deleted = await StepAsync("deleting the transfer log", () => DeleteEventsAsync(cutoffs, cancellationToken), errors);
            sendItems = await StepAsync("deleting send queue items", () => DeleteSendQueueItemsAsync(cutoffs.FinishedFiles, archive, cancellationToken), errors);
            receivedFiles = await StepAsync("deleting received file records", () => DeleteReceivedFilesAsync(cutoffs.FinishedFiles, archive, cancellationToken), errors);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add(ex.Message);
            logger.LogError(ex, "Retention failed");
        }
        finally
        {
            result = new RetentionResult(started, timeService.GetCurrentTime(), archived, outbox, deleted, sendItems,
                receivedFiles, errors.Count == 0 ? null : string.Join(" ", errors));
            LastResult = result;
            if (result.Error is null)
                LastSuccess = result.Finished;
            _running.Release();
            Changed?.Invoke();
        }

        logger.LogInformation("Retention: {Result}", result);
        return result;
    }

    private async Task<int> StepAsync(string name, Func<Task<int>> step, List<string> errors)
    {
        try
        {
            return await step();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Retention: {Step} failed", name);
            errors.Add($"{char.ToUpperInvariant(name[0])}{name[1..]} failed: {ex.Message}");
            return 0;
        }
    }

    private sealed record Watermark(DateTime Timestamp, int Id);

    /// <summary>Writes the records older than the cutoff to the archive in full and removes their details.</summary>
    private async Task<int> ArchiveEventsAsync(DateTime before, string archive, CancellationToken cancellationToken)
    {
        var count = 0;
        using var scope = serviceScopeFactory.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<ITransferEventRepository>();
        var watermark = await ReadWatermarkAsync(scope.ServiceProvider);

        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await events.GetNextOlderThanAsync(before, watermark?.Timestamp, watermark?.Id ?? 0, BatchSize, cancellationToken);
            if (batch.Count == 0)
                break;

            await RetentionArchive.AppendAsync(archive, EventsKind, batch, e => e.Timestamp, cancellationToken);
            await events.ClearDetailsAsync(batch.Where(e => e.Details is not null || e.HookParameters is not null).Select(e => e.Id).ToList(), cancellationToken);
            watermark = new Watermark(batch[^1].Timestamp, batch[^1].Id);
            await WriteWatermarkAsync(scope.ServiceProvider, watermark);
            count += batch.Count;
        }

        return count;
    }

    private static async Task<Watermark?> ReadWatermarkAsync(IServiceProvider services)
    {
        var item = (await services.GetRequiredService<ISettingsItemRepository>().GetAllAsync()).FirstOrDefault(i => i.Name == EventsExportedUntilKey);
        return item is null ? null : JsonSerializer.Deserialize<Watermark>(item.Json);
    }

    /// <summary>Stored with the settings, but not one of them: saving the settings page does not touch it.</summary>
    private static async Task WriteWatermarkAsync(IServiceProvider services, Watermark watermark)
    {
        var item = (await services.GetRequiredService<ISettingsItemRepository>().GetAllAsync()).FirstOrDefault(i => i.Name == EventsExportedUntilKey);
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var json = JsonSerializer.Serialize(watermark);
        if (item is null)
        {
            unitOfWork.AddForInsert(new SettingsItem { Name = EventsExportedUntilKey, Json = json });
        }
        else
        {
            item.Json = json;
            unitOfWork.AddForUpdate(item);
        }

        await unitOfWork.CommitAsync();
    }

    private async Task<int> DeleteEventsAsync(RetentionCutoffs cutoffs, CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        // Only what the archive has already.
        if (await ReadWatermarkAsync(scope.ServiceProvider) is not { } watermark)
            return 0;

        var events = scope.ServiceProvider.GetRequiredService<ITransferEventRepository>();
        var count = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var deleted = await events.DeleteOldAsync(watermark.Timestamp, watermark.Id, cutoffs.InformationEvents, cutoffs.Events, BatchSize, cancellationToken);
            if (deleted == 0)
                break;
            count += deleted;
        }

        return count;
    }

    /// <summary>
    /// Deletes the files in the outbox that belong only to items delivered before the cutoff. Files of no item are
    /// left alone: the directory may hold files the application did not put there.
    /// </summary>
    private async Task<int> DeleteOutboxFilesAsync(GlobalSettings settings, DateTime deliveredBefore, CancellationToken cancellationToken)
    {
        var directory = settingsService.ResolvePath(settings.OutboxDirectory);
        if (!Directory.Exists(directory))
            return 0;

        using var scope = serviceScopeFactory.CreateScope();
        var sendQueue = scope.ServiceProvider.GetRequiredService<ISendQueueItemRepository>();
        var count = 0;
        var candidates = Directory.EnumerateFiles(directory)
            .Where(f => File.GetLastWriteTime(f) < deliveredBefore);

        foreach (var chunk in candidates.Chunk(500))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in await sendQueue.GetFilesDeliveredBeforeAsync(chunk, deliveredBefore, cancellationToken))
            {
                try
                {
                    File.Delete(path);
                    count++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Retention: the outbox file {Path} cannot be deleted: {Problem}", path, ex.Message);
                }
            }
        }

        return count;
    }

    private async Task<int> DeleteSendQueueItemsAsync(DateTime createdBefore, string archive, CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var sendQueue = scope.ServiceProvider.GetRequiredService<ISendQueueItemRepository>();
        var count = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await sendQueue.GetFinishedAsync(createdBefore, BatchSize, cancellationToken);
            if (batch.Count == 0)
                break;

            await RetentionArchive.AppendAsync(archive, SendQueueKind, batch.Select(ArchivedSendQueueItem.From), i => i.Created, cancellationToken);
            count += await sendQueue.DeleteAsync(batch.Select(i => i.Id).ToList(), cancellationToken);
        }

        return count;
    }

    private async Task<int> DeleteReceivedFilesAsync(DateTime createdBefore, string archive, CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var receivedFiles = scope.ServiceProvider.GetRequiredService<IReceivedFileRepository>();
        var count = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await receivedFiles.GetFinishedAsync(createdBefore, BatchSize, cancellationToken);
            if (batch.Count == 0)
                break;

            await RetentionArchive.AppendAsync(archive, ReceivedFilesKind, batch.Select(ArchivedReceivedFile.From), f => f.Created, cancellationToken);
            count += await receivedFiles.DeleteAsync(batch.Select(f => f.Id).ToList(), cancellationToken);
        }

        return count;
    }
}
