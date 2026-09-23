using System.Text;
using Havit.Data.Patterns.UnitOfWorks;
using Microsoft.Extensions.Logging;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.Services.Oftp;
using Oftp4Net.Services.Tsl;

namespace Oftp4Net.Services.Pdx;

/// <summary>
/// Imports the OFTP2 Communication Setup (PDX) of a partner: <see cref="PlanAsync"/> shows what would change,
/// <see cref="ApplyAsync"/> creates or updates the partner. The partner is found by its SSID.
/// </summary>
public class PdxImporter(
    IPartnerRepository partners,
    ICertificateRepository certificates,
    IUnitOfWork unitOfWork,
    GlobalSettingsService settingsService,
    TslService tsl,
    ILogger<PdxImporter> logger)
{
    /// <summary>Largest datasheet accepted; real ones have a few kilobytes plus the certificates.</summary>
    public const int MaxSize = 1024 * 1024;

    /// <summary>
    /// Compares the datasheet with our station profile. With <paramref name="sender"/> (a datasheet received from
    /// that partner over OFTP) the datasheet has to describe the sender itself.
    /// </summary>
    public async Task<PdxImportPlan> PlanAsync(byte[] content, CancellationToken cancellationToken = default,
        Partner? sender = null)
    {
        var parsed = PdxParser.Parse(content);
        if (parsed.Document is not { } document || !parsed.Success)
        {
            var failed = new PdxImportPlan { Document = parsed.Document, Content = PdxParser.ToText(content) };
            failed.Errors.AddRange(parsed.Errors);
            failed.Warnings.AddRange(parsed.Warnings);
            return failed;
        }

        var settings = await settingsService.GetGlobalSettingsAsync();
        var existing = sender ?? await partners.FindBySsidAsync(document.Session.Ssid, cancellationToken);

        var plan = PdxPartnerPlanner.Plan(document, existing, new PdxPlanContext
        {
            Profile = settings.StationProfile,
            HasTlsClientCertificate = settings.OftpClientCertificateId is not null,
            HasFileSecurityCertificate = settings.FileSecurityCertificateId is not null,
            TrustAnchors = tsl.TrustAnchors,
            TrustVerificationRoots = tsl.VerificationRoots,
        });
        plan.Content = PdxParser.ToText(content);
        plan.Warnings.InsertRange(0, parsed.Warnings);

        if (sender is not null && !string.Equals(sender.SSID.Trim(), document.Session.Ssid, StringComparison.OrdinalIgnoreCase))
            plan.Errors.Insert(0, $"The datasheet describes the station {document.Session.Ssid}, but it was sent by {sender.SSID.Trim()}.");

        return plan;
    }

    /// <summary>Creates or updates the partner from an uploaded datasheet and keeps the datasheet in the history.</summary>
    public async Task<Partner> ApplyAsync(PdxImportPlan plan, string? user = null, CancellationToken cancellationToken = default)
    {
        if (plan.IsNew && plan.Document is { } document &&
            await partners.FindBySsidAsync(document.Session.Ssid, cancellationToken) is not null)
            throw new InvalidOperationException($"A partner with the SSID {document.Session.Ssid} was created in the meantime, import the datasheet again.");

        var partner = await ApplyWithoutCommitAsync(plan, cancellationToken);

        var now = DateTime.Now;
        var history = PartnerSetupDocuments.Create(plan, PartnerSetupSource.Upload, PartnerSetupSignature.None);
        history.Partner = partner;
        history.Status = PartnerSetupStatus.Applied;
        history.DecidedDate = now;
        history.DecidedBy = user;
        history.AppliedDate = now;
        unitOfWork.AddForInsert(history);

        await unitOfWork.CommitAsync(cancellationToken);
        return partner;
    }

    /// <summary>
    /// Writes the plan to its partner (a new one when the plan creates it) and registers the changes; the caller
    /// commits them.
    /// </summary>
    internal async Task<Partner> ApplyWithoutCommitAsync(PdxImportPlan plan, CancellationToken cancellationToken)
    {
        var partner = plan.Existing ?? new Partner();
        var stored = await LoadStoredCertificatesAsync(cancellationToken);
        plan.Apply(partner, pdxCertificate => GetCertificate(pdxCertificate, stored));

        if (plan.IsNew)
            unitOfWork.AddForInsert(partner);
        else
            unitOfWork.AddForUpdate(partner);

        logger.LogInformation("OFTP2 Communication Setup {DocId} applied to partner {Partner} ({Ssid}): {Changes}",
            plan.Document?.DocId, partner.Name, partner.SSID, string.Join(", ", plan.Changes.Select(c => c.Field)));

        return partner;
    }

    /// <summary>Stored certificates by their SHA-256 thumbprint; ones without a private key are preferred.</summary>
    private async Task<Dictionary<string, Certificate>> LoadStoredCertificatesAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, Certificate>(StringComparer.OrdinalIgnoreCase);
        foreach (var certificate in (await certificates.GetAllAsync(cancellationToken)).OrderBy(c => c.HasPrivateKey))
        {
            if (string.IsNullOrEmpty(certificate.Base64Data))
                continue;

            try
            {
                using var x509 = CertificateLoader.Load(certificate);
                result.TryAdd(x509.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256), certificate);
            }
            catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
            {
                // A certificate that cannot be read cannot be the one we are looking for.
            }
        }

        return result;
    }

    private Certificate GetCertificate(PdxCertificate pdxCertificate, Dictionary<string, Certificate> stored)
    {
        var thumbprint = PdxPartnerPlanner.Thumbprint(pdxCertificate.Certificate);
        if (stored.TryGetValue(thumbprint, out var existing))
            return existing;

        var certificate = CertificateLoader.CreateEntity(pdxCertificate.Certificate, pdxCertificate.Name + ".cer", null);
        unitOfWork.AddForInsert(certificate);
        stored.Add(thumbprint, certificate);
        return certificate;
    }
}

/// <summary>Creates the stored record of a datasheet.</summary>
internal static class PartnerSetupDocuments
{
    private const int TextLength = 4000;

    public static PartnerSetupDocument Create(PdxImportPlan plan, PartnerSetupSource source, PartnerSetupSignature signature) => new()
    {
        Created = DateTime.Now,
        Ssid = Shorten(plan.Document?.Session.Ssid ?? plan.Existing?.SSID ?? "", 25),
        StationName = Shorten(plan.Document?.Station.Name ?? plan.Existing?.Name ?? "", 200),
        Source = source,
        Signature = signature,
        DocId = plan.Document?.DocId ?? Guid.Empty,
        DocDate = plan.Document?.DocDate.LocalDateTime ?? DateTime.Now,
        ValidFrom = plan.Document?.ValidFrom.LocalDateTime ?? DateTime.Now,
        Content = plan.Content ?? "",
        Messages = Messages(plan),
        Changes = Changes(plan),
    };

    public static string? Messages(PdxImportPlan plan) =>
        Join(plan.Errors.Select(e => "Error: " + e).Concat(plan.Warnings.Select(w => "Warning: " + w)));

    public static string? Changes(PdxImportPlan plan) =>
        Join(plan.Changes.Select(c => c.OldValue is null ? $"{c.Field}: {c.NewValue}" : $"{c.Field}: {c.OldValue} -> {c.NewValue}"));

    public static byte[] Bytes(PartnerSetupDocument document) => Encoding.UTF8.GetBytes(document.Content);

    private static string? Join(IEnumerable<string> lines)
    {
        var text = string.Join(Environment.NewLine, lines);
        return text.Length == 0 ? null : Shorten(text, TextLength);
    }

    private static string Shorten(string value, int length) => value.Length <= length ? value : value[..(length - 1)] + "…";
}
