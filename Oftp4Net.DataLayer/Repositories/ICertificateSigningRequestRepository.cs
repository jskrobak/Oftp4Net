using Havit.Data.Patterns.Repositories;
using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Repositories;

public interface ICertificateSigningRequestRepository : IRepository<CertificateSigningRequest, int>
{
    /// <summary>Requests still waiting for the certificate signed by the certification authority, newest first.</summary>
    Task<List<CertificateSigningRequest>> GetOpenAsync(CancellationToken cancellationToken = default);
}
