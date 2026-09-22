using System.Security.Cryptography;
using System.Text;
using Havit.Data.Patterns.UnitOfWorks;
using Havit.Services.TimeServices;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;

namespace Oftp4Net.Services.Api;

/// <summary>Bearer tokens of the REST API. Tokens are stored hashed and shown in full only when created.</summary>
public class ApiTokenService(
    IApiTokenRepository tokenRepository,
    IUnitOfWork unitOfWork,
    ITimeService timeService)
{
    public const string TokenPrefix = "o4n_";

    /// <summary>Creates a token and returns it; this is the only time the full token is available.</summary>
    public async Task<(ApiToken Token, string Value)> CreateAsync(string name, DateTime? expiresAt = null,
        string? inboxWebhookUrl = null, string? webhookSecret = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("The token needs a name.");

        var value = TokenPrefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var token = new ApiToken
        {
            Name = name.Trim(),
            Prefix = value[..12],
            TokenHash = Hash(value),
            Created = timeService.GetCurrentTime(),
            ExpiresAt = expiresAt,
            InboxWebhookUrl = string.IsNullOrWhiteSpace(inboxWebhookUrl) ? null : inboxWebhookUrl.Trim(),
            WebhookSecret = string.IsNullOrWhiteSpace(webhookSecret) ? null : webhookSecret,
        };

        unitOfWork.AddForInsert(token);
        await unitOfWork.CommitAsync(cancellationToken);
        return (token, value);
    }

    /// <summary>Returns the token when the value belongs to an enabled and unexpired token, otherwise <c>null</c>.</summary>
    public async Task<ApiToken?> ValidateAsync(string value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var token = await tokenRepository.FindByHashAsync(Hash(value.Trim()), cancellationToken);
        if (token is null || !token.Enabled)
            return null;

        var now = timeService.GetCurrentTime();
        if (token.ExpiresAt is { } expiresAt && expiresAt < now)
            return null;

        // Keep the write cheap: update at most once a minute.
        if (token.LastUsed is null || now - token.LastUsed.Value > TimeSpan.FromMinutes(1))
        {
            token.LastUsed = now;
            unitOfWork.AddForUpdate(token);
            await unitOfWork.CommitAsync(cancellationToken);
        }

        return token;
    }

    public async Task<List<ApiToken>> GetAllAsync() =>
        (await tokenRepository.GetAllAsync()).OrderBy(t => t.Name).ToList();

    /// <summary>Enabled tokens with an inbox webhook, notified about received files.</summary>
    public async Task<List<ApiToken>> GetInboxWebhooksAsync(CancellationToken cancellationToken = default) =>
        (await tokenRepository.GetAllAsync(cancellationToken))
        .Where(t => t.Enabled && !string.IsNullOrEmpty(t.InboxWebhookUrl))
        .ToList();

    public async Task SaveAsync(ApiToken token)
    {
        unitOfWork.AddForUpdate(token);
        await unitOfWork.CommitAsync();
    }

    public async Task DeleteAsync(int id)
    {
        unitOfWork.AddForDelete(await tokenRepository.GetObjectAsync(id));
        await unitOfWork.CommitAsync();
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
