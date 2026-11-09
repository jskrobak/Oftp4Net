using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Havit.Services.TimeServices;
using Microsoft.Extensions.Logging;
using Oftp4Net.Core;
using Oftp4Net.Core.Protocol;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.Services.Security;
using Oftp4Net.Services.TransferEvents;

namespace Oftp4Net.Services.Api;

/// <summary>
/// Settings of a partner sent through the API; what is left out stays as it is. Passwords are not among them on
/// purpose: an administrator sets them when activating the partner.
/// </summary>
public sealed record PartnerChangeRequest
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? Ssid { get; init; }

    /// <summary>The SSID when left out for a new partner.</summary>
    public string? Sfid { get; init; }

    public string? Host { get; init; }
    public int? Port { get; init; }
    public bool? UseTls { get; init; }

    /// <summary>TLS versions: 1.0, 1.1, 1.2, 1.3.</summary>
    public IReadOnlyList<string>? TlsVersions { get; init; }

    public bool? AcceptInvalidTlsCertificate { get; init; }

    /// <summary>A stored certificate, 0 for the system trust store.</summary>
    public int? TrustedCertificateId { get; init; }

    /// <summary>Name or SSID of our identity presented when the partner calls us, empty for the listener's.</summary>
    public string? InboundIdentity { get; init; }

    /// <summary>1, 2, 4 or 5 (ODETTE-FTP 2.0).</summary>
    public int? ProtocolLevel { get; init; }

    public bool? BufferCompression { get; init; }
    public bool? Restart { get; init; }
    public bool? SignFiles { get; init; }
    public bool? EncryptFiles { get; init; }
    public bool? CompressFiles { get; init; }
    public bool? SecureAuthentication { get; init; }
    public bool? RequestSignedEndResponse { get; init; }
    public bool? RequireSignedFiles { get; init; }
    public bool? RequireEncryptedFiles { get; init; }
    public bool? RequireCompressedFiles { get; init; }

    /// <summary>01 to 09.</summary>
    public string? CipherSuite { get; init; }

    /// <summary>A stored certificate of the partner, 0 for none.</summary>
    public int? SecurityCertificateId { get; init; }

    /// <summary>Everything else in the request; refused, so that a misspelt setting does not pass unnoticed.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; init; }
}

public sealed record PartnerFieldChange(string Field, string? From, string? To);

/// <summary>What is known about the rest of the configuration when a change of a partner is checked.</summary>
public sealed record PartnerPlanContext(
    IReadOnlyCollection<Partner> Partners,
    IReadOnlyCollection<Certificate> Certificates,
    IReadOnlyCollection<Identity> Identities,
    int? FileSecurityCertificateId,
    DateTime Now);

/// <summary>A checked change of a partner, not applied yet.</summary>
public sealed class PartnerPlan
{
    public List<PartnerFieldChange> Changes { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> Errors { get; } = [];
    public bool IsValid => Errors.Count == 0;

    /// <summary>Writes the changes to the partner.</summary>
    internal Action<Partner> Apply { get; set; } = _ => { };
}

public sealed record PartnerProvisioningResult(PartnerPlan Plan, Partner? Partner, bool Applied);

/// <summary>
/// Creates and changes partners through the API. A change is first planned, so that a dry run reports exactly what
/// the real request would do. New partners are drafts: no sessions run with them until an administrator activates them.
/// </summary>
public sealed class PartnerProvisioningService(
    IPartnerRepository partners,
    ICertificateRepository certificates,
    IIdentityRepository identities,
    IDataService dataService,
    GlobalSettingsService settings,
    ListenerService listeners,
    ITransferEventLog eventLog,
    ITimeService timeService,
    ILogger<PartnerProvisioningService> logger)
{
    public async Task<PartnerProvisioningResult> CreateAsync(PartnerChangeRequest request, bool dryRun, string? actor,
        CancellationToken cancellationToken = default)
    {
        var plan = Plan(null, request, await GetContextAsync(cancellationToken));
        if (!plan.IsValid || dryRun)
            return new PartnerProvisioningResult(plan, null, false);

        var partner = new Partner { Draft = true };
        plan.Apply(partner);
        await SaveAsync(partner, plan, TransferEventType.PartnerCreated, $"Partner {partner.Name} created as a draft", actor);
        return new PartnerProvisioningResult(plan, partner, true);
    }

    public async Task<PartnerProvisioningResult> UpdateAsync(Partner partner, PartnerChangeRequest request, bool dryRun, string? actor,
        CancellationToken cancellationToken = default)
    {
        var plan = Plan(partner, request, await GetContextAsync(cancellationToken));
        if (!plan.IsValid || dryRun || plan.Changes.Count == 0)
            return new PartnerProvisioningResult(plan, partner, false);

        var name = partner.Name;
        plan.Apply(partner);
        await SaveAsync(partner, plan, TransferEventType.PartnerChanged, $"Partner {name} changed", actor);
        return new PartnerProvisioningResult(plan, partner, true);
    }

    private async Task<PartnerPlanContext> GetContextAsync(CancellationToken cancellationToken) => new(
        await partners.GetAllAsync(cancellationToken),
        await certificates.GetAllAsync(cancellationToken),
        await identities.GetAllAsync(cancellationToken),
        (await settings.GetGlobalSettingsAsync()).FileSecurityCertificateId,
        timeService.GetCurrentTime());

    private async Task SaveAsync(Partner partner, PartnerPlan plan, TransferEventType type, string what, string? actor)
    {
        await dataService.SavePartnerAsync(partner);
        await listeners.RefreshTrustedCertificatesAsync();

        var changes = string.Join("; ", plan.Changes.Select(c => $"{c.Field}: {c.From ?? "-"} -> {c.To ?? "-"}"));
        logger.LogInformation("{What} by {Actor}: {Changes}", what, actor, changes);
        var message = $"{what} by {actor}: {changes}";
        eventLog.Record(new TransferEvent
        {
            Timestamp = timeService.GetCurrentTime(),
            Category = TransferEventCategory.Configuration,
            Type = type,
            Level = TransferEventLevel.Information,
            PartnerId = partner.Id,
            PartnerName = partner.Name,
            Message = message.Length <= 1000 ? message : message[..1000],
        });
    }

    /// <summary>Checks the request against the partner (<c>null</c> for a new one) and lists what it would change.</summary>
    public static PartnerPlan Plan(Partner? existing, PartnerChangeRequest r, PartnerPlanContext context)
    {
        var plan = new PartnerPlan();
        var current = existing ?? new Partner { Draft = true };

        foreach (var field in (r.UnknownFields?.Keys ?? Enumerable.Empty<string>()).OrderBy(k => k))
            plan.Errors.Add(field.Contains("password", StringComparison.OrdinalIgnoreCase)
                ? $"'{field}': passwords are not accepted through the API, set them in the administration."
                : $"Unknown field '{field}'.");

        void Set<T>(string field, bool requested, T value, Func<Partner, T> get, Action<Partner, T> set)
        {
            if (!requested || EqualityComparer<T>.Default.Equals(get(current), value))
                return;
            plan.Changes.Add(new PartnerFieldChange(field, Show(get(current)), Show(value)));
            plan.Apply += p => set(p, value);
        }

        void SetFlag(string field, bool? value, Func<Partner, bool> get, Action<Partner, bool> set) =>
            Set(field, value.HasValue, value.GetValueOrDefault(), get, set);

        Set("name", r.Name is not null, r.Name?.Trim() ?? "", p => p.Name, (p, v) => p.Name = v);
        Set("description", r.Description is not null, EmptyToNull(r.Description), p => p.Description, (p, v) => p.Description = v);
        Set("ssid", r.Ssid is not null, r.Ssid?.Trim() ?? "", p => p.SSID, (p, v) => p.SSID = v);
        var sfid = r.Sfid ?? (existing is null ? r.Ssid : null);
        Set("sfid", sfid is not null, sfid?.Trim() ?? "", p => p.SFID, (p, v) => p.SFID = v);
        Set("host", r.Host is not null, r.Host?.Trim() ?? "", p => p.Host, (p, v) => p.Host = v);
        Set("port", r.Port.HasValue, r.Port.GetValueOrDefault(), p => p.Port, (p, v) => p.Port = v);
        SetFlag("useTls", r.UseTls, p => p.UseTls, (p, v) => p.UseTls = v);
        SetFlag("acceptInvalidTlsCertificate", r.AcceptInvalidTlsCertificate, p => p.AcceptInvalidTlsCertificate, (p, v) => p.AcceptInvalidTlsCertificate = v);
        SetFlag("bufferCompression", r.BufferCompression, p => p.BufferCompression, (p, v) => p.BufferCompression = v);
        SetFlag("restart", r.Restart, p => p.Restart, (p, v) => p.Restart = v);
        SetFlag("signFiles", r.SignFiles, p => p.SignFiles, (p, v) => p.SignFiles = v);
        SetFlag("encryptFiles", r.EncryptFiles, p => p.EncryptFiles, (p, v) => p.EncryptFiles = v);
        SetFlag("compressFiles", r.CompressFiles, p => p.CompressFiles, (p, v) => p.CompressFiles = v);
        SetFlag("secureAuthentication", r.SecureAuthentication, p => p.SecureAuthentication, (p, v) => p.SecureAuthentication = v);
        SetFlag("requestSignedEndResponse", r.RequestSignedEndResponse, p => p.RequestSignedEndResponse, (p, v) => p.RequestSignedEndResponse = v);
        SetFlag("requireSignedFiles", r.RequireSignedFiles, p => p.RequireSignedFiles, (p, v) => p.RequireSignedFiles = v);
        SetFlag("requireEncryptedFiles", r.RequireEncryptedFiles, p => p.RequireEncryptedFiles, (p, v) => p.RequireEncryptedFiles = v);
        SetFlag("requireCompressedFiles", r.RequireCompressedFiles, p => p.RequireCompressedFiles, (p, v) => p.RequireCompressedFiles = v);

        if (r.ProtocolLevel is { } level && !ProtocolLevels.IsSupported(level))
            plan.Errors.Add($"Protocol level {level} is not supported, use {string.Join(", ", ProtocolLevels.All)}.");
        else
            Set("protocolLevel", r.ProtocolLevel.HasValue, r.ProtocolLevel.GetValueOrDefault(), p => p.ProtocolLevel, (p, v) => p.ProtocolLevel = v);

        if (r.CipherSuite is { } code && CipherSuite.Supported.All(s => s.Code != code.Trim()))
            plan.Errors.Add($"Cipher suite '{code}' is not supported, use {string.Join(", ", CipherSuite.Supported.Select(s => s.Code))}.");
        else
            Set("cipherSuite", r.CipherSuite is not null, r.CipherSuite?.Trim() ?? "", p => p.FileCipherSuite, (p, v) => p.FileCipherSuite = v);

        if (r.TlsVersions is { } versions)
        {
            var tls = ParseTlsVersions(versions, plan);
            if (tls is { } value)
                Set("tlsVersions", true, value, p => p.Tls, (p, v) => p.Tls = v);
        }

        PlanCertificate("trustedCertificateId", r.TrustedCertificateId, p => p.TrustedCertificateId, (p, v) => p.TrustedCertificateId = v);
        PlanCertificate("securityCertificateId", r.SecurityCertificateId, p => p.SecurityCertificateId, (p, v) => p.SecurityCertificateId = v);

        void PlanCertificate(string field, int? id, Func<Partner, int?> get, Action<Partner, int?> set)
        {
            if (id is not { } value)
                return;

            int? target = value == 0 ? null : value;
            if (target is not null && context.Certificates.All(c => c.Id != value))
            {
                plan.Errors.Add($"{field}: certificate {value} does not exist.");
                return;
            }

            // Replacing the certificate of a running partner has to keep the previous one for files under way.
            if (existing is { Draft: false } && get(existing) is not null && get(existing) != target)
            {
                plan.Errors.Add($"{field}: the partner is active, replace its certificate with POST /partners/{{partner}}/certificate-changes.");
                return;
            }

            Set(field, true, target, get, set);
        }

        if (r.InboundIdentity is { } identityName)
            PlanIdentity(identityName.Trim());

        void PlanIdentity(string name)
        {
            if (name.Length == 0)
            {
                Set<int?>("inboundIdentity", true, null, p => p.InboundIdentityId, (p, v) => p.InboundIdentityId = v);
                return;
            }

            var found = context.Identities.Where(i => Same(i.Name, name)).ToList();
            if (found.Count == 0)
                found = context.Identities.Where(i => Same(i.SSID, name)).ToList();

            if (found.Count == 1)
                Set<int?>("inboundIdentity", true, found[0].Id, p => p.InboundIdentityId, (p, v) => p.InboundIdentityId = v);
            else if (found.Count == 0)
                plan.Errors.Add($"Unknown identity '{name}'.");
            else
                plan.Errors.Add($"Several identities have the code '{name}' ({string.Join(", ", found.Select(i => i.Name))}), name one of them.");
        }

        var candidate = Snapshot(current);
        plan.Apply(candidate);
        CheckResult(existing, candidate, plan, context);
        return plan;
    }

    private static void CheckResult(Partner? existing, Partner candidate, PartnerPlan plan, PartnerPlanContext context)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(candidate, new ValidationContext(candidate), results, validateAllProperties: true);
        plan.Errors.AddRange(results.Select(v => v.ErrorMessage ?? "Invalid value."));

        var others = context.Partners.Where(p => p.Id != existing?.Id).ToList();
        if (others.FirstOrDefault(p => Same(p.SSID, candidate.SSID)) is { } sameCode && candidate.SSID.Length > 0)
            plan.Errors.Add($"Partner {sameCode.Name} already has the SSID {candidate.SSID}.");
        if (others.Any(p => Same(p.Name, candidate.Name)) && candidate.Name.Length > 0)
            plan.Errors.Add($"A partner named {candidate.Name} already exists.");

        var needsPartnerCertificate = candidate.EncryptFiles || candidate.SecureAuthentication ||
                                      candidate.RequestSignedEndResponse || candidate.RequireSignedFiles;
        if (needsPartnerCertificate && candidate.SecurityCertificateId is null)
            plan.Errors.Add("Encryption, secure authentication and verifying signatures need securityCertificateId.");
        if ((needsPartnerCertificate || candidate.SignFiles) && candidate.ProtocolLevel < ProtocolLevels.Oftp2)
            plan.Errors.Add("File security and secure authentication need protocol level 5 (ODETTE-FTP 2.0).");
        if (candidate.SignFiles && context.FileSecurityCertificateId is null)
            plan.Warnings.Add("No file security certificate is set in the settings; identities without their own cannot sign.");

        foreach (var (field, id) in new[] { ("trustedCertificateId", candidate.TrustedCertificateId), ("securityCertificateId", candidate.SecurityCertificateId) })
            if (id is not null && context.Certificates.FirstOrDefault(c => c.Id == id) is { } certificate && certificate.ValidTo < context.Now)
                plan.Warnings.Add($"{field}: certificate {certificate.Name} expired on {certificate.ValidTo:d}.");

        if (candidate.Draft)
            plan.Warnings.Add("The partner is a draft: an administrator has to set its passwords and clear Draft before any session runs.");
    }

    private static System.Security.Authentication.SslProtocols? ParseTlsVersions(IReadOnlyList<string> versions, PartnerPlan plan)
    {
        var result = (System.Security.Authentication.SslProtocols)0;
        foreach (var version in versions)
        {
            var match = OftpDefaults.AllowedSslProtocols.Where(p => Same(TlsVersion(p), version)).ToList();
            if (match.Count == 0)
            {
                plan.Errors.Add($"TLS version '{version}' is not supported, use {string.Join(", ", OftpDefaults.AllowedSslProtocols.Select(TlsVersion))}.");
                return null;
            }

            result |= match[0];
        }

        if (result == 0)
        {
            plan.Errors.Add("At least one TLS version is needed.");
            return null;
        }

        return result;
    }

    /// <summary>The version alone, e.g. 1.2.</summary>
    public static string TlsVersion(System.Security.Authentication.SslProtocols protocol) =>
        OftpDefaults.Name(protocol).Replace(" (obsolete)", "").Replace("TLS ", "");

    /// <summary>The settings a request can change, to check their outcome without touching the partner.</summary>
    private static Partner Snapshot(Partner p) => new()
    {
        Draft = p.Draft, Name = p.Name, Description = p.Description, SSID = p.SSID, SFID = p.SFID, Host = p.Host, Port = p.Port,
        UseTls = p.UseTls, Tls = p.Tls, AcceptInvalidTlsCertificate = p.AcceptInvalidTlsCertificate,
        TrustedCertificateId = p.TrustedCertificateId, InboundIdentityId = p.InboundIdentityId, ProtocolLevel = p.ProtocolLevel,
        BufferCompression = p.BufferCompression, Restart = p.Restart, SignFiles = p.SignFiles, EncryptFiles = p.EncryptFiles,
        CompressFiles = p.CompressFiles, SecureAuthentication = p.SecureAuthentication,
        RequestSignedEndResponse = p.RequestSignedEndResponse, RequireSignedFiles = p.RequireSignedFiles,
        RequireEncryptedFiles = p.RequireEncryptedFiles, RequireCompressedFiles = p.RequireCompressedFiles,
        FileCipherSuite = p.FileCipherSuite, SecurityCertificateId = p.SecurityCertificateId,
    };

    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Show<T>(T value) => value switch
    {
        null => null,
        bool b => b ? "true" : "false",
        System.Security.Authentication.SslProtocols tls => string.Join(", ", OftpDefaults.AllowedSslProtocols.Where(p => tls.HasFlag(p)).Select(TlsVersion)),
        _ => value.ToString(),
    };
}

/// <summary>How far a loop test got: the file was sent to the partner, which sends it back.</summary>
public static class LoopTestStage
{
    public const string Queued = "Queued";
    public const string Retrying = "Retrying";
    public const string SendFailed = "SendFailed";
    public const string Refused = "Refused";
    public const string AwaitingEndResponse = "AwaitingEndResponse";
    public const string AwaitingReturn = "AwaitingReturn";
    public const string Returned = "Returned";
    public const string ReturnFailed = "ReturnFailed";

    /// <param name="status">State of the file we sent.</param>
    /// <param name="returned">States of the files received back since it was sent.</param>
    public static string From(SendStatus status, IReadOnlyCollection<ReceiveStatus> returned)
    {
        switch (status)
        {
            case SendStatus.FAILED:
                return SendFailed;
            case SendStatus.ERROR:
                return Retrying;
            case SendStatus.NOT_DELIVERED:
                return Refused;
            case SendStatus.NEW:
                return Queued;
        }

        if (returned.Any(s => s is ReceiveStatus.CONFIRMED or ReceiveStatus.RECEIVED))
            return Returned;
        if (returned.Any(s => s is ReceiveStatus.FAILED or ReceiveStatus.NOT_DELIVERED))
            return ReturnFailed;
        return status == SendStatus.DELIVERED ? AwaitingReturn : AwaitingEndResponse;
    }
}
