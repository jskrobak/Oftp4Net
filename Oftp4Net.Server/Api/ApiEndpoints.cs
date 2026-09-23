using Oftp4Net.Core.Protocol;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oftp4Net.DataLayer.Filters;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.Services;
using Oftp4Net.Services.Oftp;
using Oftp4Net.Services.Pdx;
using Oftp4Net.Services.Api;

namespace Oftp4Net.Server.Api;

/// <summary>REST API for integrations, authenticated with a bearer token (see Settings / API tokens).</summary>
public static class ApiEndpoints
{
    private const int MaxPageSize = 500;

    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1")
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = ApiTokenAuthenticationHandler.SchemeName })
            .WithTags("Oftp4Net");

        MapOutbox(api, app);
        MapInbox(api);
        MapReference(api);
    }

    #region Outbox

    private static void MapOutbox(RouteGroupBuilder api, WebApplication app)
    {
        var maxFileSize = app.Configuration.GetValue("Upload:MaxOutboxFileSizeMB", 512L) * 1024 * 1024;

        api.MapPost("/outbox", async (
                HttpContext httpContext,
                IFormFile file,
                [FromForm] string partner,
                [FromForm] string identity,
                [FromForm] string? destination,
                [FromForm] string? virtualFileName,
                [FromForm] string? description,
                [FromForm] string? reference,
                [FromForm] string? webhookUrl,
                [FromForm] string? webhookSecret,
                IPartnerRepository partners,
                IIdentityRepository identities,
                IDataService dataService,
                OutboxStorage outbox,
                WebhookDispatcher webhooks,
                SendService sendService,
                CancellationToken cancellationToken) =>
            {
                if (file.Length == 0)
                    return Results.BadRequest(new ApiError("The file is empty."));

                var partnerEntity = (await partners.GetAllAsync(cancellationToken))
                    .FirstOrDefault(p => Matches(p.SSID, partner) || Matches(p.Name, partner));
                if (partnerEntity is null)
                    return Results.BadRequest(new ApiError($"Unknown partner '{partner}'."));

                var identityEntity = (await identities.GetAllAsync(cancellationToken))
                    .FirstOrDefault(i => Matches(i.SSID, identity) || Matches(i.Name, identity));
                if (identityEntity is null)
                    return Results.BadRequest(new ApiError($"Unknown identity '{identity}'."));

                // A sub-station of the partner (e.g. a plant) with its own SFID; empty for the partner itself.
                string? destinationSfid = null;
                if (!string.IsNullOrWhiteSpace(destination) && !Matches(partnerEntity.SFID, destination))
                {
                    var subStation = StationSettings.FindSubStation(partnerEntity, destination);
                    if (subStation is null)
                        return Results.BadRequest(new ApiError($"'{destination}' is neither the SFID of partner {partnerEntity.Name} nor of one of its sub-stations."));
                    destinationSfid = subStation.SFID;
                }

                if (!string.IsNullOrEmpty(webhookUrl) && !webhooks.IsAllowed(webhookUrl, out var webhookError))
                    return Results.BadRequest(new ApiError(webhookError));

                var name = (virtualFileName ?? OutboxStorage.SuggestVirtualFileName(file.FileName)).Trim();
                if (name.Length is 0 or > 26)
                    return Results.BadRequest(new ApiError("The virtual file name must have 1 to 26 characters."));

                var item = new SendQueueItem
                {
                    PartnerId = partnerEntity.Id,
                    IdentityId = identityEntity.Id,
                    DestinationSfid = destinationSfid,
                    VirtualFileName = name,
                    FilePath = await outbox.SaveAsync(file, cancellationToken),
                    Description = description,
                    Reference = reference,
                    WebhookUrl = webhookUrl,
                    WebhookSecret = webhookSecret,
                    Status = SendStatus.NEW,
                };

                await dataService.SaveSendQueueItemAsync(item);
                sendService.Trigger();

                item.Partner = partnerEntity;
                item.Identity = identityEntity;
                return Results.Created($"/api/v1/outbox/{item.Id}", QueueItemDto.From(item));
            })
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(maxFileSize),
                new RequestFormLimitsAttribute { MultipartBodyLengthLimit = maxFileSize })
            .WithSummary("Puts a file into the send queue and optionally registers a webhook for its progress.");

        api.MapGet("/outbox", async (
                ISendQueueItemRepository repository,
                SendStatus? status, string? partner, string? reference, DateTime? from, DateTime? to,
                int? skip, int? take, CancellationToken cancellationToken) =>
            {
                var filter = new SendQueueFilter
                {
                    Status = status, PartnerSsid = partner, Reference = reference, From = from, To = to,
                };
                var page = await repository.GetListAsync(filter, Skip(skip), Take(take), cancellationToken);
                return Results.Ok(new ApiPage<QueueItemDto>(page.Data.Select(QueueItemDto.From).ToList(), page.TotalCount));
            })
            .WithSummary("Lists queued, sent and failed files.");

        api.MapGet("/outbox/{id:int}", async (int id, ISendQueueItemRepository repository, CancellationToken cancellationToken) =>
                await repository.FindWithRefsAsync(id, cancellationToken) is { } item
                    ? Results.Ok(QueueItemDto.From(item))
                    : Results.NotFound())
            .WithSummary("Detail of a queued file.");

        api.MapDelete("/outbox/{id:int}", async (int id, ISendQueueItemRepository repository, IDataService dataService,
                OutboxStorage outbox, CancellationToken cancellationToken) =>
            {
                var item = await repository.FindWithRefsAsync(id, cancellationToken);
                if (item is null)
                    return Results.NotFound();
                if (item.Status is SendStatus.SENT or SendStatus.DELIVERED)
                    return Results.Conflict(new ApiError("The file was already transferred."));

                await dataService.DeleteSendQueueItemAsync(item);
                await outbox.DeleteIfInOutboxAsync(item.FilePath);
                return Results.NoContent();
            })
            .WithSummary("Removes a file that has not been transferred yet.");

        api.MapPost("/outbox/{id:int}/retry", async (int id, ISendQueueItemRepository repository, IDataService dataService,
                SendService sendService, CancellationToken cancellationToken) =>
            {
                var item = await repository.FindWithRefsAsync(id, cancellationToken);
                if (item is null)
                    return Results.NotFound();

                await dataService.RequeueSendQueueItemAsync(item);
                sendService.Trigger();
                return Results.Ok(QueueItemDto.From(item));
            })
            .WithSummary("Puts a failed or finished file back into the queue.");
    }

    #endregion

    #region Inbox

    private static void MapInbox(RouteGroupBuilder api)
    {
        api.MapGet("/inbox", async (
                IReceivedFileRepository repository,
                ReceiveStatus? status, string? partner, bool? onlyNew, DateTime? from, DateTime? to,
                int? skip, int? take, CancellationToken cancellationToken) =>
            {
                var filter = new ReceivedFileFilter
                {
                    Status = status, PartnerSsid = partner, OnlyNotFetched = onlyNew ?? false, From = from, To = to,
                };
                var page = await repository.GetListAsync(filter, Skip(skip), Take(take), cancellationToken);
                return Results.Ok(new ApiPage<ReceivedFileDto>(page.Data.Select(ReceivedFileDto.From).ToList(), page.TotalCount));
            })
            .WithSummary("Lists received files; onlyNew=true returns files not fetched through the API yet.");

        api.MapGet("/inbox/{id:int}", async (int id, IReceivedFileRepository repository, CancellationToken cancellationToken) =>
                await repository.FindWithRefsAsync(id, cancellationToken) is { } file
                    ? Results.Ok(ReceivedFileDto.From(file))
                    : Results.NotFound())
            .WithSummary("Detail of a received file.");

        api.MapGet("/inbox/{id:int}/content", async (int id, IReceivedFileRepository repository, CancellationToken cancellationToken) =>
            {
                var file = await repository.FindWithRefsAsync(id, cancellationToken);
                if (file is null || !File.Exists(file.FilePath))
                    return Results.NotFound();

                return Results.File(Path.GetFullPath(file.FilePath), "application/octet-stream", file.VirtualFileName);
            })
            .WithSummary("Downloads the content of a received file.");

        api.MapPost("/inbox/{id:int}/fetched", async (int id, IReceivedFileRepository repository, IDataService dataService,
                CancellationToken cancellationToken) =>
            {
                var file = await repository.FindWithRefsAsync(id, cancellationToken);
                if (file is null)
                    return Results.NotFound();

                await dataService.MarkReceivedFileFetchedAsync(file);
                return Results.Ok(ReceivedFileDto.From(file));
            })
            .WithSummary("Marks a received file as fetched, so it is no longer returned by onlyNew=true.");

        api.MapPost("/inbox/{id:int}/not-delivered", async (int id, NotDeliveredRequest request,
                IReceivedFileRepository repository, IDataService dataService, CancellationToken cancellationToken) =>
            {
                var file = await repository.FindWithRefsAsync(id, cancellationToken);
                if (file is null)
                    return Results.NotFound();

                try
                {
                    await dataService.ReportReceivedFileNotDeliveredAsync(file,
                        string.IsNullOrWhiteSpace(request.ReasonCode) ? AnswerReasonCodes.UnspecifiedReason : request.ReasonCode,
                        request.ReasonText);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
                }

                return Results.Ok(ReceivedFileDto.From(file));
            })
            .WithSummary("Reports a received file as not delivered to its final destination; the partner is told with a NERP.");
    }

    #endregion

    #region Partners, identities, events and status

    private static void MapReference(RouteGroupBuilder api)
    {
        api.MapGet("/partners", async (IPartnerRepository repository, CancellationToken cancellationToken) =>
                Results.Ok((await repository.GetAllAsync(cancellationToken))
                    .Select(p => new PartyDto(p.Id, p.Name, p.SSID, p.SFID)).ToList()))
            .WithSummary("Lists partners that can be used when sending.");

        api.MapGet("/identities", async (IIdentityRepository repository, CancellationToken cancellationToken) =>
                Results.Ok((await repository.GetAllAsync(cancellationToken))
                    .Select(i => new PartyDto(i.Id, i.Name, i.SSID, i.SFID)).ToList()))
            .WithSummary("Lists our identities that can be used when sending.");

        api.MapGet("/events", async (
                ITransferEventRepository repository,
                TransferEventCategory? category, string? partner, string? virtualFileName,
                TransferEventLevel? minimumLevel, bool? includeArchive, DateTime? from, DateTime? to,
                int? skip, int? take, CancellationToken cancellationToken) =>
            {
                var filter = new TransferEventFilter
                {
                    Category = category, PartnerName = partner, VirtualFileName = virtualFileName,
                    MinimumLevel = minimumLevel, IncludeArchive = includeArchive ?? false, From = from, To = to,
                };
                var page = await repository.GetListAsync(filter, Skip(skip), Take(take), cancellationToken);
                return Results.Ok(new ApiPage<TransferEventDto>(page.Data.Select(TransferEventDto.From).ToList(), page.TotalCount));
            })
            .WithSummary("Reads the transfer log.");

        api.MapGet("/pdx/{identity}", async (string identity, DateTimeOffset? validFrom,
                IIdentityRepository identities, PdxExporter exporter, CancellationToken cancellationToken) =>
            {
                var identityEntity = (await identities.GetAllAsync(cancellationToken))
                    .FirstOrDefault(i => Matches(i.SSID, identity) || Matches(i.Name, identity));
                if (identityEntity is null)
                    return Results.NotFound(new ApiError($"Unknown identity '{identity}'."));

                var export = await exporter.ExportAsync(identityEntity.Id, validFrom, cancellationToken);
                return Results.File(export.Content, "application/xml", export.FileName);
            })
            .WithSummary("Our OFTP2 Communication Setup (PDX datasheet) of an identity, e.g. for a new partner.");

        api.MapGet("/status", async (
                SendService sendService, ListenerService listenerService,
                ISendQueueItemRepository queue, IReceivedFileRepository received, CancellationToken cancellationToken) =>
            {
                var pending = await queue.GetListAsync(new SendQueueFilter { Status = SendStatus.NEW }, 0, 1, cancellationToken);
                var failed = await queue.GetListAsync(new SendQueueFilter { Status = SendStatus.FAILED }, 0, 1, cancellationToken);
                var notFetched = await received.GetListAsync(new ReceivedFileFilter { OnlyNotFetched = true }, 0, 1, cancellationToken);

                return Results.Ok(new StatusDto(
                    sendService.IsRunning, sendService.IsPaused, sendService.LastRun,
                    listenerService.Status.Select(s => new ListenerStatusDto(s.Name, s.EndPoint, s.Running, s.Error, s.ActiveSessions)).ToList(),
                    pending.TotalCount, failed.TotalCount, notFetched.TotalCount));
            })
            .WithSummary("Status of the services, listeners and queues.");
    }

    #endregion

    private static bool Matches(string? value, string other) => string.Equals(value?.Trim(), other.Trim(), StringComparison.OrdinalIgnoreCase);

    private static int Skip(int? skip) => Math.Max(0, skip ?? 0);

    private static int Take(int? take) => Math.Clamp(take ?? 100, 1, MaxPageSize);
}

public record ApiError(string Error);

public record ApiPage<T>(IReadOnlyList<T> Items, int TotalCount);

public record PartyDto(int Id, string Name, string Ssid, string Sfid);

public record QueueItemDto(int Id, string Status, string VirtualFileName, string? Reference, string? Description,
    string? PartnerName, string? PartnerSsid, string? IdentityName, DateTime Created, string? FileDate, string? FileTime,
    DateTime? SentDate, DateTime? DeliveredDate, int RetryCount, DateTime? NextRetry, string? LastError, string? WebhookUrl,
    string? DestinationSfid)
{
    public static QueueItemDto From(SendQueueItem i) => new(i.Id, i.Status.ToString(), i.VirtualFileName, i.Reference,
        i.Description, i.Partner?.Name, i.Partner?.SSID, i.Identity?.Name, i.Created, i.FileDate, i.FileTime,
        i.SentDate, i.DeliveredDate, i.RetryCount, i.Status == SendStatus.ERROR ? i.NextRetry : null, i.LastError, i.WebhookUrl,
        string.IsNullOrEmpty(i.DestinationSfid) ? i.Partner?.SFID : i.DestinationSfid);
}

public record ReceivedFileDto(int Id, string Status, string VirtualFileName, string? PartnerName, string? PartnerSsid,
    string Originator, string Destination, string? Description, string? UserData, long Size, DateTime Created,
    string FileDate, string FileTime, DateTime? ConfirmedDate, DateTime? FetchedDate)
{
    public static ReceivedFileDto From(ReceivedFile f) => new(f.Id, f.Status.ToString(), f.VirtualFileName, f.Partner?.Name,
        f.Partner?.SSID, f.Originator, f.Destination, f.Description, f.UserData, f.Size, f.Created, f.FileDate, f.FileTime,
        f.ConfirmedDate, f.FetchedDate);
}

/// <summary>Body of the request reporting a received file as not delivered to its final destination.</summary>
/// <param name="ReasonCode">Answer reason code sent in the NERP (01 - 99), by default 99 (unspecified).</param>
/// <param name="ReasonText">Description sent to the partner with the response.</param>
public record NotDeliveredRequest(string? ReasonCode, string? ReasonText);

public record TransferEventDto(int Id, DateTime Timestamp, string Category, string Level, string Type, string Message,
    string? PartnerName, string? VirtualFileName, long? DurationMs, int? QueueItemId, int? ReceivedFileId, bool IsArchived)
{
    public static TransferEventDto From(TransferEvent e) => new(e.Id, e.Timestamp, e.Category.ToString(), e.Level.ToString(),
        e.Type.ToString(), e.Message, e.PartnerName, e.VirtualFileName, e.DurationMs, e.SendQueueItemId, e.ReceivedFileId,
        e.IsArchived);
}

public record ListenerStatusDto(string Name, string EndPoint, bool Running, string? Error, int ActiveSessions);

public record StatusDto(bool SendServiceRunning, bool SendServicePaused, DateTime? SendServiceLastRun,
    IReadOnlyList<ListenerStatusDto> Listeners, int PendingFiles, int FailedFiles, int FilesToFetch);
