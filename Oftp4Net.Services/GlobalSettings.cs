using System.ComponentModel.DataAnnotations;
using Oftp4Net.Core.Transport;
using Oftp4Net.Services.Pdx;
using Oftp4Net.Services.Security;
using Oftp4Net.Services.Tsl;

namespace Oftp4Net.Services;

public class GlobalSettings
{
    /// <summary>How certificate revocation is checked, as the transport expects it.</summary>
    public CertificateRevocationPolicy RevocationPolicy => new()
    {
        Check = CheckCertificateRevocation,
        Require = RequireRevocationInformation,
    };

    /// <summary>Certificate with private key presented to partners as TLS client certificate.</summary>
    [SettingsItem]
    public int? OftpClientCertificateId { get; set; } = null;

    /// <summary>Directory where received files are stored.</summary>
    [SettingsItem]
    [Required]
    public string ReceiveDirectory { get; set; } = "received";

    /// <summary>Directory where files uploaded to the send queue are stored.</summary>
    [SettingsItem]
    [Required]
    public string OutboxDirectory { get; set; } = "outbox";

    /// <summary>How often the send queue is checked.</summary>
    [SettingsItem]
    [Range(5, 86400)]
    public int SendIntervalSeconds { get; set; } = 60;

    /// <summary>Sessions with different partners run at the same time up to this number.</summary>
    [SettingsItem]
    [Range(1, 100)]
    public int MaxParallelSessions { get; set; } = 8;

    /// <summary>Failed transfers are retried up to this number of times, then marked as FAILED.</summary>
    [SettingsItem]
    [Range(0, 1000)]
    public int MaxRetryCount { get; set; } = 10;

    /// <summary>Data exchange buffer size proposed in SSID.</summary>
    [SettingsItem]
    [Range(128, 99999)]
    public int ExchangeBufferSize { get; set; } = 65536;

    /// <summary>Credit (DATA buffers before CDT) proposed in SSID.</summary>
    [SettingsItem]
    [Range(1, 999)]
    public int Credit { get; set; } = 64;

    /// <summary>Transfer log records older than this are archived (hidden unless the complete archive is shown).</summary>
    [SettingsItem]
    [Range(1, 3650)]
    public int ArchiveEventsAfterDays { get; set; } = 90;

    /// <summary>
    /// Certificate with private key used for file level security: outgoing files and End to End Responses are
    /// signed with it, incoming ones are decrypted with it. Give its public part to your partners.
    /// </summary>
    [SettingsItem]
    public int? FileSecurityCertificateId { get; set; } = null;

    /// <summary>
    /// Signing, compression and encryption of files is done in memory, so files larger than this are not
    /// secured and their transfer fails with an error.
    /// </summary>
    [SettingsItem]
    [Range(1, 4096)]
    public int MaxSecuredFileSizeMb { get; set; } = 100;

    /// <summary>
    /// Read the revocation lists (CRL) of the issuers and refuse a certificate that is on one. It applies to the
    /// certificates of TLS connections and of the trust list; a certificate pinned for a partner is trusted by
    /// itself and is not checked.
    /// </summary>
    [SettingsItem]
    public bool CheckCertificateRevocation { get; set; } = true;

    /// <summary>
    /// Refuse a certificate whose revocation state cannot be found out at all, for example because the list is
    /// unreachable. Off by default, so that an unreachable list does not stop the transfers.
    /// </summary>
    [SettingsItem]
    public bool RequireRevocationInformation { get; set; }

    /// <summary>Seconds to wait for a response from the partner.</summary>
    [SettingsItem]
    [Range(10, 3600)]
    public int ResponseTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Our security policy, compared with the OFTP2 Communication Setup (PDX) of a partner when it is imported.
    /// </summary>
    [SettingsItem]
    public StationProfile StationProfile { get; set; } = new();

    /// <summary>Which OFTP2 Communication Setups received from partners over OFTP are applied without approval.</summary>
    [SettingsItem]
    public PdxAutoApply PdxAutoApply { get; set; } = PdxAutoApply.SignedOnly;

    /// <summary>
    /// How often the revocation list of a certification authority is downloaded again (Odette OP08 2.6: the
    /// standard period between updates).
    /// </summary>
    [SettingsItem]
    [Range(1, 720)]
    public int CrlRefreshHours { get; set; } = 24;

    /// <summary>
    /// How old the revocation list of a certificate may be at most (Odette OP08 2.6 recommends 15 days). A
    /// certificate whose list could not be refreshed within this period is not used until it has been read again.
    /// </summary>
    [SettingsItem]
    [Range(1, 365)]
    public int CrlMaxAgeDays { get; set; } = 15;

    /// <summary>
    /// Which certificates received from partners over OFTP (ODETTE_CERTIFICATE_DELIVER and its siblings) are taken
    /// over without the administrator.
    /// </summary>
    [SettingsItem]
    public CertificateExchangeMode CertificateExchange { get; set; } = CertificateExchangeMode.Known;

    /// <summary>
    /// Downloads the Odette Trust Service Status List (TSL): certificates issued by the certification authorities
    /// listed there are trusted for TLS and in partner datasheets.
    /// </summary>
    [SettingsItem]
    public bool TslEnabled { get; set; } = true;

    /// <summary>Address of the TSL; Odette publishes a production and a test list.</summary>
    [SettingsItem]
    [Required]
    [Url]
    public string TslUrl { get; set; } = TslService.ProductionUrl;

    /// <summary>How often the TSL is downloaded again.</summary>
    [SettingsItem]
    [Range(1, 168)]
    public int TslRefreshHours { get; set; } = 24;

    /// <summary>
    /// SHA-256 thumbprint of the certificate the TSL has to be signed with. Taken over from the first list that is
    /// downloaded; a list signed with another certificate is refused until this value is cleared.
    /// </summary>
    [SettingsItem]
    [StringLength(64)]
    public string? TslSignerThumbprint { get; set; }

}
