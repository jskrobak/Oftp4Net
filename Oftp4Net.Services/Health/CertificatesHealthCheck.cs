using Havit.Services.TimeServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;

namespace Oftp4Net.Services.Health;

/// <summary>
/// No certificate in use expired or expires within <see cref="WarningDays"/> days: ours (TLS client, listeners,
/// file security, per station and purpose) and those of the partners. Certificates stored but not used, and those
/// kept only for a roll-over, are left out, and so is a certificate of a partner that a scheduled certificate change
/// replaces before it expires.
/// </summary>
public sealed class CertificatesHealthCheck(IServiceScopeFactory serviceScopeFactory,
    GlobalSettingsService settingsService, ITimeService timeService) : IHealthCheck
{
    public const int WarningDays = 30;

    /// <param name="ReplacedAt">When a scheduled certificate change replaces the certificate in this use.</param>
    public sealed record CertificateUse(Certificate Certificate, string Usage, DateTime? ReplacedAt = null);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetGlobalSettingsAsync();

        using var scope = serviceScopeFactory.CreateScope();
        var certificates = (await scope.ServiceProvider.GetRequiredService<ICertificateRepository>().GetAllAsync(cancellationToken))
            .ToDictionary(c => c.Id);
        var listeners = await scope.ServiceProvider.GetRequiredService<IListenerRepository>().GetEnabledWithRefsAsync(cancellationToken);
        var partners = await scope.ServiceProvider.GetRequiredService<IPartnerRepository>().GetAllAsync(cancellationToken);
        var identities = await scope.ServiceProvider.GetRequiredService<IIdentityRepository>().GetAllAsync(cancellationToken);
        var changes = await scope.ServiceProvider.GetRequiredService<ICertificateChangeRepository>().GetScheduledAsync(cancellationToken);

        var uses = new List<(int? Id, string Usage, DateTime? ReplacedAt)>
        {
            (settings.OftpClientCertificateId, "TLS client certificate", null),
            (settings.FileSecurityCertificateId, "file security certificate", null),
        };
        uses.AddRange(listeners.Where(l => l.UseTls).Select(l => (l.CertificateId, $"server certificate of listener {l.Name}", (DateTime?)null)));
        foreach (var partner in partners)
        {
            DateTime? Replaced(params PartnerCertificateUsage[] usages) => changes
                .Where(c => c.PartnerId == partner.Id && usages.Contains(c.Usage))
                .Min(c => (DateTime?)c.ActivateAt);

            var securityReplaced = Replaced(PartnerCertificateUsage.Security, PartnerCertificateUsage.SecurityAndTls);
            uses.Add((partner.TrustedCertificateId, $"TLS certificate trusted for partner {partner.Name}",
                Replaced(PartnerCertificateUsage.Tls, PartnerCertificateUsage.SecurityAndTls)));
            uses.Add((partner.SecurityCertificateId, $"file security certificate of partner {partner.Name}", securityReplaced));
            // A change of the file security certificate replaces the per-station certificates that are the same one.
            uses.AddRange(partner.Certificates.Select(a => ((int?)a.CertificateId, $"{a.Usage} certificate of partner {partner.Name}{Station(a)}",
                a.CertificateId == partner.SecurityCertificateId ? securityReplaced : null)));
        }
        foreach (var identity in identities)
            uses.AddRange(identity.Certificates.Select(a => ((int?)a.CertificateId, $"{a.Usage} certificate of identity {identity.Name}{Station(a)}", (DateTime?)null)));

        return Evaluate(
            uses.Where(u => u.Id is { } id && certificates.ContainsKey(id))
                .Select(u => new CertificateUse(certificates[u.Id!.Value], u.Usage, u.ReplacedAt))
                .ToList(),
            timeService.GetCurrentTime());
    }

    private static string Station(CertificateAssignment assignment) =>
        string.IsNullOrEmpty(assignment.Sfid) ? "" : $" for {assignment.Sfid}";

    public static HealthCheckResult Evaluate(IReadOnlyCollection<CertificateUse> uses, DateTime now)
    {
        var certificates = uses
            .GroupBy(u => u.Certificate.Id)
            .Select(g => (Certificate: g.First().Certificate,
                Usages: string.Join(", ", g.Select(u => u.Usage).Distinct()),
                // Replaced in time only when every use of it is.
                ReplacedAt: g.All(u => u.ReplacedAt is not null) ? g.Max(u => u.ReplacedAt) : null))
            .OrderBy(c => c.Certificate.ValidTo)
            .ToList();

        if (certificates.Count == 0)
            return HealthCheckResult.Healthy("No certificate is in use.");

        var data = certificates.ToDictionary(c => $"{c.Certificate.Name} (#{c.Certificate.Id})",
            c => (object)$"valid to {c.Certificate.ValidTo:s}; {c.Usages}");

        var problems = certificates
            .Where(c => c.Certificate.ValidTo < now.AddDays(WarningDays))
            .Where(c => c.ReplacedAt is null || c.ReplacedAt > c.Certificate.ValidTo)
            .Select(c => c.Certificate.ValidTo < now
                ? $"Certificate {c.Certificate.Name} ({c.Usages}) expired on {c.Certificate.ValidTo:d}."
                : $"Certificate {c.Certificate.Name} ({c.Usages}) expires on {c.Certificate.ValidTo:d}.")
            .ToList();

        if (problems.Count > 0)
            return HealthCheckResult.Degraded(string.Join(" ", problems), data: data);

        return HealthCheckResult.Healthy(
            $"{certificates.Count} certificate(s) in use, the first one expires on {certificates[0].Certificate.ValidTo:d}.", data);
    }
}
