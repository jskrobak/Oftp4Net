using Havit.Data.Patterns.Repositories;
using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Repositories;

public interface ICertificateAnnouncementLinkRepository : IRepository<CertificateAnnouncementLink, int>
{
    /// <summary>The questionnaires of the announcement of a change.</summary>
    Task<List<CertificateAnnouncementLink>> GetByChangeAsync(int changeId, CancellationToken cancellationToken = default);

    /// <summary>The questionnaire with the token, with its change; <c>null</c> when there is none.</summary>
    Task<CertificateAnnouncementLink?> FindByTokenAsync(string token, CancellationToken cancellationToken = default);
}
