using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Oftp4Net.Core.Protocol;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;

namespace Oftp4Net.Services;

/// <summary>What a test file is sent as; <see cref="DestinationSfid"/> names a sub-station, empty for the partner.</summary>
public sealed class TestFileRequest
{
    public int PartnerId { get; set; }
    public int IdentityId { get; set; }
    public string? DestinationSfid { get; set; }
    public string VirtualFileName { get; set; } = TestFileService.DefaultVirtualFileName;
    public string Content { get; set; } = TestFileService.DefaultContent;
}

/// <summary>
/// Sends a short text file to a partner, e.g. to show a new partner that files arrive, or after a change of its
/// settings. The file goes through the send queue as any other, with the partner's settings (security, encoding).
/// </summary>
public sealed class TestFileService(
    IDataService dataService,
    OutboxStorage outbox,
    SendService sendService,
    IServiceScopeFactory scopeFactory,
    ILogger<TestFileService> logger)
{
    public const string DefaultVirtualFileName = "TEST";
    public const string DefaultContent = "This is test from artipa. support@artipa.com";

    /// <summary>Why the request cannot be sent, or <c>null</c>.</summary>
    public static string? Validate(TestFileRequest request)
    {
        var name = request.VirtualFileName.Trim();
        if (name.Length is 0 or > 26)
            return "The virtual file name must have 1 to 26 characters.";
        // SFIDDSN takes upper case letters, digits and a few separators (ODETTE-FTP 2, 5.3.3).
        if (name.Any(c => !(c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_' or '&' or '(' or ')' or '/' or ' ')))
            return "The virtual file name may contain upper case letters, digits, spaces and . - _ & ( ) / only.";
        if (string.IsNullOrWhiteSpace(request.Content))
            return "The test file needs some content.";
        return null;
    }

    /// <summary>Puts the test file into the send queue and starts the send service; returns the queue item.</summary>
    public async Task<SendQueueItem> QueueAsync(TestFileRequest request, string? user, CancellationToken cancellationToken = default)
    {
        if (Validate(request) is { } problem)
            throw new InvalidOperationException(problem);

        var name = request.VirtualFileName.Trim();
        var content = Encoding.UTF8.GetBytes(request.Content.TrimEnd() + "\r\n");
        var item = new SendQueueItem
        {
            PartnerId = request.PartnerId,
            IdentityId = request.IdentityId,
            DestinationSfid = string.IsNullOrWhiteSpace(request.DestinationSfid) ? null : request.DestinationSfid.Trim(),
            VirtualFileName = name,
            Format = FileFormats.Unstructured,
            MaxRecordSize = 0,
            FilePath = await outbox.SaveAsync(content, $"{name}.txt", cancellationToken),
            Description = $"Test file sent by {user ?? "unknown"}",
            Status = SendStatus.NEW,
        };

        await dataService.SaveSendQueueItemAsync(item);
        sendService.Trigger();

        logger.LogInformation("{User} queued test file {Item} ({Name}, {Size} bytes) for partner {PartnerId}{Station}",
            user ?? "unknown", item.Id, name, content.Length, request.PartnerId,
            item.DestinationSfid is null ? "" : $" station {item.DestinationSfid}");
        return item;
    }

    /// <summary>The queue item as it is in the database now (read in a scope of its own, not from a cache).</summary>
    public async Task<SendQueueItem?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISendQueueItemRepository>().FindWithRefsAsync(id, cancellationToken);
    }
}
