using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oftp4Net.Domain;
using Oftp4Net.Services.TransferEvents;

namespace Oftp4Net.Services.Api;

public interface IWebhookDispatcher
{
    /// <summary>Queues a webhook call. Returns immediately; delivery and retries happen in the background.</summary>
    void Dispatch(string url, string? secret, WebhookPayload payload);
}

/// <summary>Body of the webhook request. Only properties with a value are sent.</summary>
public sealed class WebhookPayload
{
    public required string Event { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public int? QueueItemId { get; init; }
    public int? ReceivedFileId { get; init; }
    public string? Reference { get; init; }
    public string? VirtualFileName { get; init; }
    public string? FileDate { get; init; }
    public string? FileTime { get; init; }
    public long? FileSize { get; init; }
    public string? PartnerName { get; init; }
    public string? PartnerSsid { get; init; }
    public string? Originator { get; init; }
    public string? Destination { get; init; }
    public string? Status { get; init; }
    public string? Error { get; init; }
    public DateTime? SentDate { get; init; }
    public DateTime? DeliveredDate { get; init; }
}

/// <summary>
/// Calls webhooks registered through the REST API (per file or per token) in the background, with retries.
/// The request body is JSON; when a secret is configured, the header <c>X-Oftp4Net-Signature</c> carries its
/// HMAC-SHA256 as <c>sha256=&lt;hex&gt;</c>.
/// </summary>
public sealed class WebhookDispatcher(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ITransferEventLog transferEvents,
    ILogger<WebhookDispatcher> logger) : BackgroundService, IWebhookDispatcher
{
    public const string SignatureHeader = "X-Oftp4Net-Signature";
    public const string EventHeader = "X-Oftp4Net-Event";
    public const string HttpClientName = "webhooks";

    private static readonly TimeSpan[] DefaultRetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Channel<(string Url, string? Secret, WebhookPayload Payload)> _queue =
        Channel.CreateBounded<(string, string?, WebhookPayload)>(
            new BoundedChannelOptions(10_000) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>Delays before retrying a failed call (configuration <c>Webhooks:RetryDelaysSeconds</c>, e.g. "5,30,120").</summary>
    private TimeSpan[] RetryDelays
    {
        get
        {
            var configured = configuration["Webhooks:RetryDelaysSeconds"];
            if (string.IsNullOrWhiteSpace(configured))
                return DefaultRetryDelays;

            return configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => double.TryParse(value, out var seconds) ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero)
                .ToArray();
        }
    }

    /// <summary>Webhook URLs pointing to loopback or private networks are refused unless this is enabled.</summary>
    private bool AllowPrivateNetworks => configuration.GetValue("Webhooks:AllowPrivateNetworks", false);

    public void Dispatch(string url, string? secret, WebhookPayload payload)
    {
        if (!_queue.Writer.TryWrite((url, secret, payload)))
            logger.LogError("Webhook {Event} to {Url} was not sent: the queue is full", payload.Event, url);
    }

    /// <summary>Checks that the URL can be called (http/https and, unless allowed, not a private address).</summary>
    public bool IsAllowed(string url, out string error)
    {
        error = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "The webhook URL must be an absolute http or https URL.";
            return false;
        }

        if (AllowPrivateNetworks)
            return true;

        if (IsPrivateHost(uri.DnsSafeHost))
        {
            error = "The webhook URL points to a private or loopback address (see Webhooks:AllowPrivateNetworks).";
            return false;
        }

        return true;
    }

    private static bool IsPrivateHost(string host)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var parsed))
        {
            addresses = [parsed];
        }
        else
        {
            try
            {
                addresses = Dns.GetHostAddresses(host);
            }
            catch (SocketException)
            {
                // Cannot resolve: let the request fail later with a proper error.
                return false;
            }
        }

        return addresses.Any(a => IPAddress.IsLoopback(a)
                                  || a.IsIPv6LinkLocal
                                  || a.IsIPv6UniqueLocal
                                  || (a.AddressFamily == AddressFamily.InterNetwork && IsPrivateIPv4(a.GetAddressBytes())));
    }

    private static bool IsPrivateIPv4(byte[] bytes) =>
        bytes[0] == 10
        || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
        || (bytes[0] == 192 && bytes[1] == 168)
        || (bytes[0] == 169 && bytes[1] == 254);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (url, secret, payload) in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await DeliverAsync(url, secret, payload, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Webhook {Event} to {Url} failed", payload.Event, url);
            }
        }
    }

    private async Task DeliverAsync(string url, string? secret, WebhookPayload payload, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(payload, JsonOptions);
        var client = httpClientFactory.CreateClient(HttpClientName);
        var retryDelays = RetryDelays;

        for (var attempt = 0; ; attempt++)
        {
            string failure;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                request.Headers.TryAddWithoutValidation(EventHeader, payload.Event);
                if (!string.IsNullOrEmpty(secret))
                    request.Headers.TryAddWithoutValidation(SignatureHeader, Sign(body, secret));

                using var response = await client.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    Record(TransferEventType.WebhookDelivered, TransferEventLevel.Information, payload,
                        $"Webhook {payload.Event} delivered to {url} ({(int)response.StatusCode})", null);
                    return;
                }

                failure = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                failure = ex.Message;
            }

            if (attempt >= retryDelays.Length)
            {
                logger.LogError("Webhook {Event} to {Url} failed after {Attempts} attempts: {Failure}",
                    payload.Event, url, attempt + 1, failure);
                Record(TransferEventType.WebhookFailed, TransferEventLevel.Error, payload,
                    $"Webhook {payload.Event} to {url} failed after {attempt + 1} attempts: {failure}", body);
                return;
            }

            logger.LogWarning("Webhook {Event} to {Url} failed ({Failure}), retrying in {Delay}",
                payload.Event, url, failure, retryDelays[attempt]);
            await Task.Delay(retryDelays[attempt], cancellationToken);
        }
    }

    public static string Sign(string body, string secret) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

    private void Record(TransferEventType type, TransferEventLevel level, WebhookPayload payload, string message, string? details)
    {
        transferEvents.Record(new TransferEvent
        {
            Category = TransferEventCategory.Hook,
            Type = type,
            Level = level,
            Message = message,
            Details = details,
            PartnerName = payload.PartnerName,
            VirtualFileName = payload.VirtualFileName,
            FileDate = payload.FileDate,
            FileTime = payload.FileTime,
            SendQueueItemId = payload.QueueItemId,
            ReceivedFileId = payload.ReceivedFileId,
        });
    }
}
