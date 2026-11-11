using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Oftp4Net.Services.Mail;

/// <summary>A file attached to a message.</summary>
public sealed record MailAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>A message to send; sender, Reply-To and the blind copy come from <see cref="MailOptions"/>.</summary>
public sealed record OutgoingMail(
    IReadOnlyList<string> To, string Subject, string TextBody, IReadOnlyList<MailAttachment> Attachments)
{
    public IReadOnlyList<string> Cc { get; init; } = [];
}

/// <summary>Thrown when a message could not be sent; the text says why.</summary>
public sealed class MailException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Sends e-mail through the configured provider (<see cref="MailOptions"/>): SMTP or Microsoft Graph.</summary>
public class MailService(IOptionsMonitor<MailOptions> options, IHttpClientFactory httpClientFactory, ILogger<MailService> logger)
{
    public const string HttpClientName = "mail";

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private (string Token, DateTimeOffset Expires)? _token;

    /// <summary>What is missing for sending, <c>null</c> when it is configured.</summary>
    public string? Problem => Validate(options.CurrentValue);

    /// <summary>How the messages go out, for the administration.</summary>
    public string Description
    {
        get
        {
            var o = options.CurrentValue;
            return o.Provider switch
            {
                MailProvider.Smtp => $"SMTP {o.Smtp.Host}:{o.Smtp.Port} as {Sender(o)}",
                MailProvider.Graph => $"Microsoft Graph from {o.Graph.Mailbox} as {Sender(o)}",
                _ => "not configured",
            } + (string.IsNullOrWhiteSpace(o.ReplyTo) ? "" : $", answers to {o.ReplyTo}")
              + (string.IsNullOrWhiteSpace(o.Bcc) ? "" : $", copy to {o.Bcc}");
        }
    }

    public async Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken = default)
    {
        var o = options.CurrentValue;
        if (Validate(o) is { } problem)
            throw new MailException(problem);
        if (mail.To.Count == 0)
            throw new MailException("The message has no recipient.");

        if (o.Provider == MailProvider.Smtp)
            await SendSmtpAsync(o, mail, cancellationToken);
        else
            await SendGraphAsync(o, mail, cancellationToken);

        logger.LogInformation("E-mail \"{Subject}\" sent to {To}", mail.Subject, string.Join(", ", mail.To.Concat(mail.Cc)));
    }

    public static string? Validate(MailOptions o) => o.Provider switch
    {
        MailProvider.None => "Sending e-mail is not configured (section Mail, Provider Smtp or Graph).",
        MailProvider.Smtp when string.IsNullOrWhiteSpace(o.Smtp.Host) => "Mail:Smtp:Host is missing.",
        MailProvider.Smtp when string.IsNullOrWhiteSpace(o.From) => "Mail:From is missing.",
        MailProvider.Graph when string.IsNullOrWhiteSpace(o.Graph.TenantId) || string.IsNullOrWhiteSpace(o.Graph.ClientId)
                                || string.IsNullOrWhiteSpace(o.Graph.ClientSecret) =>
            "Mail:Graph needs TenantId, ClientId and ClientSecret.",
        MailProvider.Graph when string.IsNullOrWhiteSpace(o.Graph.Mailbox) => "Mail:Graph:Mailbox is missing.",
        _ => null,
    };

    private static string Sender(MailOptions o) =>
        !string.IsNullOrWhiteSpace(o.From) ? o.From.Trim() : o.Graph.Mailbox?.Trim() ?? "";

    /// <summary>The MIME message as it goes to the SMTP server.</summary>
    public static MimeMessage BuildMimeMessage(MailOptions o, OutgoingMail mail)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(o.FromName ?? "", Sender(o)));
        if (!string.IsNullOrWhiteSpace(o.ReplyTo))
            message.ReplyTo.Add(MailboxAddress.Parse(o.ReplyTo.Trim()));
        foreach (var to in mail.To)
            message.To.Add(MailboxAddress.Parse(to));
        foreach (var cc in mail.Cc)
            message.Cc.Add(MailboxAddress.Parse(cc));
        if (!string.IsNullOrWhiteSpace(o.Bcc))
            message.Bcc.Add(MailboxAddress.Parse(o.Bcc.Trim()));
        message.Subject = mail.Subject;

        var body = new BodyBuilder { TextBody = mail.TextBody };
        foreach (var attachment in mail.Attachments)
            body.Attachments.Add(attachment.FileName, attachment.Content, ContentType.Parse(attachment.ContentType));
        message.Body = body.ToMessageBody();
        return message;
    }

    private static async Task SendSmtpAsync(MailOptions o, OutgoingMail mail, CancellationToken cancellationToken)
    {
        var message = BuildMimeMessage(o, mail);
        using var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(o.Smtp.Host!.Trim(), o.Smtp.Port, o.Smtp.Security switch
            {
                SmtpSecurity.None => SecureSocketOptions.None,
                SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
                SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
                _ => SecureSocketOptions.Auto,
            }, cancellationToken);
            if (!string.IsNullOrWhiteSpace(o.Smtp.UserName))
                await client.AuthenticateAsync(o.Smtp.UserName, o.Smtp.Password ?? "", cancellationToken);
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not MailException)
        {
            throw new MailException($"The SMTP server {o.Smtp.Host}:{o.Smtp.Port} refused the message: {ex.Message}", ex);
        }
    }

    /// <summary>The body of the Graph call <c>POST /users/{mailbox}/sendMail</c>.</summary>
    public static JsonObject BuildGraphMessage(MailOptions o, OutgoingMail mail)
    {
        static JsonArray Recipients(IEnumerable<string> addresses) =>
            new(addresses.Select(a => (JsonNode)new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = a } }).ToArray());

        var message = new JsonObject
        {
            ["subject"] = mail.Subject,
            ["body"] = new JsonObject { ["contentType"] = "Text", ["content"] = mail.TextBody },
            ["toRecipients"] = Recipients(mail.To),
        };
        if (!string.IsNullOrWhiteSpace(o.From))
            message["from"] = new JsonObject
            {
                ["emailAddress"] = new JsonObject { ["address"] = o.From.Trim(), ["name"] = o.FromName ?? "" },
            };
        if (mail.Cc.Count > 0)
            message["ccRecipients"] = Recipients(mail.Cc);
        if (!string.IsNullOrWhiteSpace(o.Bcc))
            message["bccRecipients"] = Recipients([o.Bcc.Trim()]);
        if (!string.IsNullOrWhiteSpace(o.ReplyTo))
            message["replyTo"] = Recipients([o.ReplyTo.Trim()]);
        if (mail.Attachments.Count > 0)
            message["attachments"] = new JsonArray(mail.Attachments.Select(a => (JsonNode)new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.fileAttachment",
                ["name"] = a.FileName,
                ["contentType"] = a.ContentType,
                ["contentBytes"] = Convert.ToBase64String(a.Content),
            }).ToArray());

        return new JsonObject { ["message"] = message, ["saveToSentItems"] = true };
    }

    private async Task SendGraphAsync(MailOptions o, OutgoingMail mail, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var token = await GetTokenAsync(o, client, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{o.Graph.Endpoint.TrimEnd('/')}/v1.0/users/{Uri.EscapeDataString(o.Graph.Mailbox!.Trim())}/sendMail")
        {
            Content = JsonContent(BuildGraphMessage(o, mail)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new MailException($"Microsoft Graph refused the message ({(int)response.StatusCode}): " +
                                    await ErrorAsync(response, cancellationToken));
    }

    private async Task<string> GetTokenAsync(MailOptions o, HttpClient client, CancellationToken cancellationToken)
    {
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_token is { } cached && cached.Expires > DateTimeOffset.UtcNow.AddMinutes(5))
                return cached.Token;

            using var response = await client.PostAsync(
                $"{o.Graph.Authority.TrimEnd('/')}/{Uri.EscapeDataString(o.Graph.TenantId!.Trim())}/oauth2/v2.0/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = o.Graph.ClientId!.Trim(),
                    ["client_secret"] = o.Graph.ClientSecret!,
                    ["scope"] = $"{o.Graph.Endpoint.TrimEnd('/')}/.default",
                    ["grant_type"] = "client_credentials",
                }), cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new MailException($"Signing in to Microsoft Graph failed ({(int)response.StatusCode}): " +
                                        await ErrorAsync(response, cancellationToken));

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var token = json.RootElement.GetProperty("access_token").GetString()!;
            var seconds = json.RootElement.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 3600;
            _token = (token, DateTimeOffset.UtcNow.AddSeconds(seconds));
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static StringContent JsonContent(JsonNode node) =>
        new(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

    /// <summary>The message of a Graph or token error, without the whole answer.</summary>
    private static async Task<string> ErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.TryGetProperty("error", out var error))
                return error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)
                    ? message.GetString() ?? text
                    : json.RootElement.TryGetProperty("error_description", out var description) ? description.GetString() ?? text : error.ToString();
        }
        catch (JsonException)
        {
        }

        return text.Length > 300 ? text[..300] : text;
    }
}
