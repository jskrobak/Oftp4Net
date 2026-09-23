using Havit.Blazor.Components.Web.Bootstrap;
using Havit.Data.EntityFrameworkCore;
using Havit.Data.EntityFrameworkCore.Patterns.Caching;
using Havit.Data.EntityFrameworkCore.Patterns.Repositories;
using Havit.Data.EntityFrameworkCore.Patterns.SoftDeletes;
using Havit.Data.Patterns.DataLoaders;
using Havit.Data.Patterns.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Oftp4Net.DataLayer.Filters;
using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Repositories;

public class PartnerRepository(
    IDbContext dbContext,
    IEntityKeyAccessor<Partner, int> entityKeyAccessor,
    IDataLoader dataLoader,
    ISoftDeleteManager softDeleteManager,
    IEntityCacheManager entityCacheManager,
    IRepositoryQueryProvider<Partner, int> repositoryQueryProvider)
    : DbRepository<Partner, int>(dbContext, entityKeyAccessor, dataLoader, softDeleteManager, entityCacheManager, repositoryQueryProvider), IPartnerRepository
{
    public async Task<DataFragment<Partner>> GetFragmentAsync(PartnerFilter filter, GridDataProviderRequest<Partner> request,
        CancellationToken cancellationToken = default)
    {
        var filtered = filter.Apply(Data);

        var cnt = await filtered.CountAsync(cancellationToken);

        var data = await filtered.ApplyGridDataProviderRequest<Partner>(request).ToListAsync(cancellationToken: cancellationToken);

        return new DataFragment<Partner>()
        {
            Data = data,
            TotalCount = cnt
        };
    }

    public async Task<List<Certificate>> GetTrustedCertificatesAsync(CancellationToken cancellationToken = default)
    {
        // The certificate a partner used before a roll-over is kept as well: the partner may still present it in
        // TLS until it starts using the new one (Odette OP08 2.5 F).
        var trusted = Data
            .Where(p => p.TrustedCertificate != null)
            .Select(p => p.TrustedCertificate!);
        var previous = Data
            .Where(p => p.PreviousSecurityCertificate != null)
            .Select(p => p.PreviousSecurityCertificate!);

        return await trusted.Union(previous).Distinct().ToListAsync(cancellationToken);
    }

    public async Task<Partner?> FindBySsidAsync(string ssid, CancellationToken cancellationToken = default)
    {
        return await Data
            .Include(i => i.TrustedCertificate)
            .Include(i => i.SecurityCertificate)
            .FirstOrDefaultAsync(i => i.SSID == ssid, cancellationToken);
    }
}