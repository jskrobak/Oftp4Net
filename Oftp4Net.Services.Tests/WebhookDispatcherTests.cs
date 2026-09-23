using System.Net;
using System.Threading.Channels;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Oftp4Net.Domain;
using Oftp4Net.Services.Api;
using Oftp4Net.Services.TransferEvents;

namespace Oftp4Net.Services.Tests;

public class WebhookDispatcherTests
{
    private static WebhookDispatcher CreateDispatcher(bool allowPrivateNetworks, out RecordingEventLog events)
    {
        events = new RecordingEventLog();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Webhooks:AllowPrivateNetworks"] = allowPrivateNetworks.ToString(),
                // Do not wait between retries in tests.
                ["Webhooks:RetryDelaysSeconds"] = "0,0,0",
            })
            .Build();

        return new WebhookDispatcher(new SimpleHttpClientFactory(), configuration, events, NullLogger<WebhookDispatcher>.Instance);
    }

    [Fact]
    public void SignatureIsHmacSha256OfTheBody()
    {
        // Verified against: printf '{"event":"file.sent"}' | openssl dgst -sha256 -hmac "s3cret"
        Assert.Equal("sha256=" + Convert.ToHexStringLower(
                System.Security.Cryptography.HMACSHA256.HashData("s3cret"u8.ToArray(), """{"event":"file.sent"}"""u8.ToArray())),
            WebhookDispatcher.Sign("""{"event":"file.sent"}""", "s3cret"));
    }

    [Theory]
    [InlineData("http://127.0.0.1:9000/hook", false)]
    [InlineData("http://192.168.1.10/hook", false)]
    [InlineData("ftp://example.com/hook", false)]
    [InlineData("not a url", false)]
    [InlineData("https://example.com/hook", true)]
    public void PrivateAndInvalidUrlsAreRefused(string url, bool expected)
    {
        var dispatcher = CreateDispatcher(allowPrivateNetworks: false, out _);

        Assert.Equal(expected, dispatcher.IsAllowed(url, out var error));
        Assert.Equal(expected, error.Length == 0);
    }

    [Fact]
    public void PrivateUrlsAreAllowedWhenConfigured()
    {
        var dispatcher = CreateDispatcher(allowPrivateNetworks: true, out _);

        Assert.True(dispatcher.IsAllowed("http://127.0.0.1:9000/hook", out _));
    }

    [Fact]
    public async Task RequestIsRetriedAndSigned()
    {
        using var server = new TestWebhookServer();
        var dispatcher = CreateDispatcher(allowPrivateNetworks: true, out var events);
        server.StatusCodes.Enqueue(HttpStatusCode.InternalServerError);
        server.StatusCodes.Enqueue(HttpStatusCode.OK);

        await dispatcher.StartAsync(CancellationToken.None);
        dispatcher.Dispatch(server.Url, "s3cret", new WebhookPayload { Event = "file.sent", QueueItemId = 7 });

        var first = await server.WaitForRequestAsync();
        var second = await server.WaitForRequestAsync();

        // The event is recorded after the response is read.
        var recordedEvents = await events.WaitForEventAsync();
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.Equal(first.Body, second.Body);
        Assert.Contains("\"event\":\"file.sent\"", first.Body);
        Assert.Contains("\"queueItemId\":7", first.Body);
        // Properties without a value are left out.
        Assert.DoesNotContain("reference", first.Body);
        Assert.Equal(WebhookDispatcher.Sign(first.Body, "s3cret"), first.Signature);
        Assert.Equal("file.sent", first.Event);

        var recorded = Assert.Single(recordedEvents);
        Assert.Equal(TransferEventType.WebhookDelivered, recorded.Type);
    }

    [Fact]
    public async Task FailedDeliveryIsRecorded()
    {
        using var server = new TestWebhookServer();
        var dispatcher = CreateDispatcher(allowPrivateNetworks: true, out var events);
        for (var i = 0; i < 4; i++)
            server.StatusCodes.Enqueue(HttpStatusCode.BadGateway);

        await dispatcher.StartAsync(CancellationToken.None);
        dispatcher.Dispatch(server.Url, null, new WebhookPayload { Event = "file.delivered" });

        for (var i = 0; i < 4; i++)
            await server.WaitForRequestAsync();

        var recordedEvents = await events.WaitForEventAsync();
        await dispatcher.StopAsync(CancellationToken.None);

        var recorded = Assert.Single(recordedEvents);
        Assert.Equal(TransferEventType.WebhookFailed, recorded.Type);
        Assert.Equal(TransferEventLevel.Error, recorded.Level);
    }

    private sealed class RecordingEventLog : ITransferEventLog
    {
        private readonly List<TransferEvent> _events = [];

        /// <summary>What was recorded so far; a copy, because the dispatcher records on its own thread.</summary>
        public IReadOnlyList<TransferEvent> Events
        {
            get
            {
                lock (_events)
                    return _events.ToList();
            }
        }

        public void Record(TransferEvent transferEvent)
        {
            lock (_events)
                _events.Add(transferEvent);
        }

        /// <summary>Waits until something was recorded; the dispatcher does it on its own thread.</summary>
        public async Task<IReadOnlyList<TransferEvent>> WaitForEventAsync()
        {
            for (var i = 0; i < 100 && Events.Count == 0; i++)
                await Task.Delay(100);
            return Events;
        }
    }

    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(10) };
    }

    private sealed record ReceivedRequest(string Body, string? Signature, string? Event);

    /// <summary>Minimal HTTP server answering with the queued status codes.</summary>
    private sealed class TestWebhookServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Channel<ReceivedRequest> _requests = Channel.CreateUnbounded<ReceivedRequest>();

        public TestWebhookServer()
        {
            // A free port is looked for: another process may hold the one we picked.
            for (var attempt = 0; ; attempt++)
            {
                Url = $"http://127.0.0.1:{Random.Shared.Next(20_000, 60_000)}/hook/";
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add(Url);
                try
                {
                    _listener.Start();
                    break;
                }
                catch (HttpListenerException) when (attempt < 20)
                {
                }
            }

            _ = AcceptAsync();
        }

        public string Url { get; }

        public Queue<HttpStatusCode> StatusCodes { get; } = new();

        private async Task AcceptAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                await _requests.Writer.WriteAsync(new ReceivedRequest(body,
                    context.Request.Headers[WebhookDispatcher.SignatureHeader],
                    context.Request.Headers[WebhookDispatcher.EventHeader]));

                context.Response.StatusCode = (int)(StatusCodes.Count > 0 ? StatusCodes.Dequeue() : HttpStatusCode.OK);
                context.Response.Close();
            }
        }

        public async Task<ReceivedRequest> WaitForRequestAsync(TimeSpan? timeout = null)
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
            return await _requests.Reader.ReadAsync(cts.Token);
        }

        public void Dispose() => _listener.Close();
    }
}
