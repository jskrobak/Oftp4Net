using System.ComponentModel.DataAnnotations;

namespace Oftp4Net.Services;

public class GlobalSettings
{
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

    /// <summary>Failed transfers are retried up to this number of times, then marked as FAILED.</summary>
    [SettingsItem]
    [Range(0, 1000)]
    public int MaxRetryCount { get; set; } = 10;

    /// <summary>Data exchange buffer size proposed in SSID.</summary>
    [SettingsItem]
    [Range(128, 99999)]
    public int ExchangeBufferSize { get; set; } = 4096;

    /// <summary>Credit (DATA buffers before CDT) proposed in SSID.</summary>
    [SettingsItem]
    [Range(1, 999)]
    public int Credit { get; set; } = 64;

    /// <summary>Seconds to wait for a response from the partner.</summary>
    [SettingsItem]
    [Range(10, 3600)]
    public int ResponseTimeoutSeconds { get; set; } = 180;

}
