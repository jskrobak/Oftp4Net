using Havit.Services.TimeServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;

namespace Oftp4Net.Services.Health;

/// <summary>
/// No certificate in use expired or expires within <see cref="WarningDays"/> days: ours (TLS client, listeners,
/// file security, per station and purpose) and those of the partners. Certificates stored but not used, and those
/// kept only for a roll-over, are left out.
/// </summary>
public sealed class CertificatesHealthCheck(IServiceScopeFactory serviceScopeFactory,
    GlobalSettingsService settingsService, ITimeService timeService) : IHealthCheck
{
    public const int WarningDays = 30;

    public sealed record CertificateUse(Certificate Certificate, string Usage);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetGlobalSettingsAsync();

        using var scope = serviceScopeFactory.CreateScope();
        var certificates = (await scope.ServiceProvider.GetRequiredService<ICertificateRepository>().GetAllAsync(cancellationToken))
            .ToDictionary(c => c.Id);
        var listeners = await scope.ServiceProvider.GetRequiredService<IListenerRepository>().GetEnabledWithRefsAsync(cancellationToken);
        var partners = await scope.ServiceProvider.GetRequiredService<IPartnerRepository>().GetAllAsync(cancellationToken);
        var identities = await scope.ServiceProvider.GetRequiredService<IIdentityRepository>().GetAllAsync(cancellationToken);

        var uses = new List<(int? Id, string Usage)>
        {
            (settings.OftpClientCertificateId, "TLS client certificate"),
            (settings.FileSecurityCertificateId, "file security certificate"),
        };
        uses.AddRange(listeners.Where(l => l.UseTls).Select(l => (l.CertificateId, $"server certificate of listener {l.Name}")));
        foreach (var partner in partners)
        {
            uses.Add((partner.TrustedCertificateId, $"TLS certificate trusted for partner {partner.Name}"));
            uses.Add((partner.SecurityCertificateId, $"file security certificate of partner {partner.Name}"));
            uses.AddRange(partner.Certificates.Select(a => ((int?)a.CertificateId, $"{a.Usage} certificate of partner {partner.Name}{Station(a)}")));
        }
        foreach (var identity in identities)
            uses.AddRange(identity.Certificates.Select(a => ((int?)a.CertificateId, $"{a.Usage} certificate of identity {identity.Name}{Station(a)}")));

        return Evaluate(
            uses.Where(u => u.Id is { } id && certificates.ContainsKey(id))
                .Select(u => new CertificateUse(certificates[u.Id!.Value], u.Usage))
                .ToList(),
            timeService.GetCurrentTime());
    }

    private static string Station(CertificateAssignment assignment) =>
        string.IsNullOrEmpty(assignment.Sfid) ? "" : $" for {assignment.Sfid}";

    public static HealthCheckResult Evaluate(IReadOnlyCollection<CertificateUse> uses, DateTime now)
    {
        var certificates = uses
            .GroupBy(u => u.Certificate.Id)
            .Select(g => (Certificate: g.First().Certificate, Usages: string.Join(", ", g.Select(u => u.Usage).Distinct())))
            .OrderBy(c => c.Certificate.ValidTo)
            .ToList();

        if (certificates.Count == 0)
            return HealthCheckResult.Healthy("No certificate is in use.");

        var data = certificates.ToDictionary(c => $"{c.Certificate.Name} (#{c.Certificate.Id})",
            c => (object)$"valid to {c.Certificate.ValidTo:s}; {c.Usages}");

        var problems = certificates
            .Where(c => c.Certificate.ValidTo < now.AddDays(WarningDays))
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
