using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Oftp4Net.Services;
using Oftp4Net.Services.Security;

namespace Oftp4Net.Server.Api;

/// <summary>
/// Our certificates published without sign in, when <c>Settings → Publish our certificates</c> is on:
/// <c>/cert</c> (a page for people), <c>/cert/index.json</c> (the same for scripts) and the downloads
/// <c>/cert/{key}.cer</c> (DER), <c>/cert/{key}.pem</c> and <c>/cert/{key}-chain.pem</c> (with the authorities).
/// Switched off, every address answers 404 as if it did not exist.
/// </summary>
public static class PublicCertificateEndpoints
{
    public const string RateLimitPolicy = "public-certificates";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>A partner opens the page a few times; anything beyond that per address is refused for a minute.</summary>
    public static void AddPublicCertificateRateLimit(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

    public static void MapPublicCertificates(this WebApplication app)
    {
        var group = app.MapGroup("/cert").AllowAnonymous().RequireRateLimiting(RateLimitPolicy).ExcludeFromDescription();

        group.MapGet("", async (PublicCertificateService service, ApplicationTimeService time, CancellationToken cancellationToken) =>
            await service.GetAsync(cancellationToken) is { } page
                ? Results.Content(Html(page, time), "text/html; charset=utf-8")
                : Results.NotFound());

        group.MapGet("/index.json", async (PublicCertificateService service, ApplicationTimeService time, CancellationToken cancellationToken) =>
            await service.GetAsync(cancellationToken) is { } page
                ? Results.Json(Json(page, time), JsonOptions)
                : Results.NotFound());

        group.MapGet("/{file}", async (string file, PublicCertificateService service, CancellationToken cancellationToken) =>
        {
            var (key, kind) = file switch
            {
                _ when file.EndsWith("-chain.pem", StringComparison.OrdinalIgnoreCase) => (file[..^"-chain.pem".Length], "chain"),
                _ when file.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) => (file[..^".pem".Length], "pem"),
                _ when file.EndsWith(".cer", StringComparison.OrdinalIgnoreCase) => (file[..^".cer".Length], "cer"),
                _ => ("", ""),
            };
            if (kind.Length == 0 || await service.FindAsync(key, kind == "chain", cancellationToken) is not { } found)
                return Results.NotFound();

            using var certificate = found.Certificate;
            if (kind == "cer")
                return Results.File(certificate.RawData, "application/pkix-cert", $"{key}.cer");

            var pem = new StringBuilder(certificate.ExportCertificatePem()).Append('\n');
            foreach (var authority in found.Chain)
            {
                pem.Append(authority.ExportCertificatePem()).Append('\n');
                authority.Dispose();
            }

            return Results.File(Encoding.ASCII.GetBytes(pem.ToString()), "application/x-pem-file", file);
        });
    }

    private static object Json(PublishedCertificates page, ApplicationTimeService time) => new
    {
        page.Company,
        page.Host,
        page.Port,
        page.OdetteIds,
        Current = page.Current.Select(c => JsonCertificate(c, time)),
        Next = page.Next.Select(c => JsonCertificate(c, time)),
    };

    private static object JsonCertificate(PublishedCertificate c, ApplicationTimeService time) => new
    {
        c.Subject,
        c.Issuer,
        c.SerialNumber,
        c.Sha256,
        c.Sha1,
        ValidFrom = new DateTimeOffset(c.ValidFrom.ToUniversalTime(), TimeSpan.Zero),
        ValidTo = new DateTimeOffset(c.ValidTo.ToUniversalTime(), TimeSpan.Zero),
        c.UsedFor,
        UsedFrom = c.UsedFrom is { } from ? time.ToInstant(from) : (DateTimeOffset?)null,
        c.Replaces,
        Download = new { Cer = $"{c.Key}.cer", Pem = $"{c.Key}.pem", Chain = $"{c.Key}-chain.pem" },
    };

    private static string Html(PublishedCertificates page, ApplicationTimeService time)
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
        var title = string.IsNullOrWhiteSpace(page.Company) ? "Our OFTP2 certificates" : $"OFTP2 certificates of {page.Company}";

        var html = new StringBuilder();
        html.Append($$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex">
            <title>{{E(title)}}</title>
            <style>
            :root { color-scheme: light dark; --fg: #1d2330; --muted: #5b6474; --bg: #fff; --card: #f5f7fa; --line: #dde2ea; --accent: #2f5b9b; }
            @media (prefers-color-scheme: dark) { :root { --fg: #e6e9ef; --muted: #9aa3b2; --bg: #15181e; --card: #1e232b; --line: #2e3540; --accent: #8fb3ea; } }
            body { margin: 0; background: var(--bg); color: var(--fg); font: 15px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; }
            main { max-width: 860px; margin: 0 auto; padding: 24px 16px 48px; }
            h1 { font-size: 1.5rem; margin: 0 0 4px; } h2 { font-size: 1.1rem; margin: 28px 0 8px; }
            p { margin: 4px 0; } .muted { color: var(--muted); }
            .card { background: var(--card); border: 1px solid var(--line); border-radius: 8px; padding: 14px 16px; margin: 10px 0; }
            .card.next { border-color: var(--accent); }
            dl { display: grid; grid-template-columns: 150px 1fr; gap: 2px 12px; margin: 8px 0; }
            dt { color: var(--muted); } dd { margin: 0; overflow-wrap: anywhere; }
            code { font: 13px/1.4 ui-monospace, SFMono-Regular, Menlo, monospace; }
            a { color: var(--accent); } .downloads a { margin-right: 14px; }
            @media (max-width: 560px) { dl { grid-template-columns: 1fr; } dt { margin-top: 6px; } }
            </style>
            </head>
            <body><main>
            <h1>{{E(title)}}</h1>
            """);

        if (!string.IsNullOrWhiteSpace(page.Host))
            html.Append($"<p>OFTP2 host <code>{E(page.Host)}{(page.Port is { } port ? $":{port}" : "")}</code></p>");
        if (page.OdetteIds.Count > 0)
            html.Append($"<p>Odette ID (SSID) {string.Join(", ", page.OdetteIds.Select(id => $"<code>{E(id)}</code>"))}</p>");
        html.Append("<p class=\"muted\">Compare the fingerprint of a certificate with the one we sent you before you import it.</p>");

        if (page.Next.Count > 0)
        {
            html.Append("<h2>Next certificate</h2>");
            foreach (var certificate in page.Next)
                AppendCertificate(html, certificate, time, next: true);
        }

        html.Append("<h2>Certificate in use</h2>");
        if (page.Current.Count == 0)
            html.Append("<p class=\"muted\">None is published.</p>");
        foreach (var certificate in page.Current)
            AppendCertificate(html, certificate, time, next: false);

        html.Append("<p class=\"muted\" style=\"margin-top:28px\">Also as JSON: <a href=\"/cert/index.json\">index.json</a></p>");
        html.Append("</main></body></html>");
        return html.ToString();
    }

    private static void AppendCertificate(StringBuilder html, PublishedCertificate c, ApplicationTimeService time, bool next)
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
        html.Append(next ? "<div class=\"card next\">" : "<div class=\"card\">");
        if (c.UsedFrom is { } from)
            html.Append($"<p><strong>Used from {E(time.ToInstant(from).ToString("yyyy-MM-dd HH:mm 'UTC'zzz"))}</strong>" +
                        (c.Replaces is { } replaced ? $" in place of <code>{E(replaced)}</code>" : "") + "</p>");
        html.Append("<dl>");
        html.Append($"<dt>Subject</dt><dd>{E(c.Subject)}</dd>");
        html.Append($"<dt>Issuer</dt><dd>{E(c.Issuer)}</dd>");
        html.Append($"<dt>Serial number</dt><dd><code>{E(c.SerialNumber)}</code></dd>");
        html.Append($"<dt>Valid</dt><dd>{c.ValidFrom.ToUniversalTime():yyyy-MM-dd} – {c.ValidTo.ToUniversalTime():yyyy-MM-dd}</dd>");
        html.Append($"<dt>SHA-256</dt><dd><code>{E(c.Sha256)}</code></dd>");
        html.Append($"<dt>SHA-1</dt><dd><code>{E(c.Sha1)}</code></dd>");
        html.Append($"<dt>Used for</dt><dd>{E(string.Join(", ", c.UsedFor))}</dd>");
        html.Append($"<dt>Key</dt><dd><code>{E(c.Key)}</code></dd>");
        html.Append("</dl>");
        html.Append($"<p class=\"downloads\"><a href=\"/cert/{c.Key}.cer\">{c.Key}.cer</a> <a href=\"/cert/{c.Key}.pem\">.pem</a> " +
                    $"<a href=\"/cert/{c.Key}-chain.pem\">.pem with chain</a></p>");
        html.Append("</div>");
    }
}
