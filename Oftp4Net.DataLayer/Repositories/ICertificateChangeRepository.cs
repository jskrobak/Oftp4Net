using Havit.Data.Patterns.Repositories;
using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Repositories;

public interface ICertificateChangeRepository : IRepository<CertificateChange, int>
{
    /// <summary>Scheduled changes whose time has come, the earliest first, with partner and certificate.</summary>
    Task<List<CertificateChange>> GetDueAsync(DateTime now, CancellationToken cancellationToken = default);

    /// <summary>All changes of the partner with their certificates, the latest first.</summary>
    Task<List<CertificateChange>> GetByPartnerAsync(int partnerId, CancellationToken cancellationToken = default);

    /// <summary>When the next scheduled change is due, <c>null</c> when none is scheduled.</summary>
    Task<DateTime?> GetNextScheduledAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes still waiting for their time, with partner and certificate, the earliest first.</summary>
    Task<List<CertificateChange>> GetScheduledAsync(CancellationToken cancellationToken = default);

    /// <summary>The scheduled changes and the latest <paramref name="take"/> others, with their certificates, the latest time first.</summary>
    Task<List<CertificateChange>> GetOverviewAsync(int take, CancellationToken cancellationToken = default);
}
