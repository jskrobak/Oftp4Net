using Havit.Blazor.Components.Web.Bootstrap;
using Oftp4Net.DataLayer;
using Oftp4Net.DataLayer.Filters;
using Oftp4Net.Domain;

namespace Oftp4Net.Services;

public interface IDataService
{
    Task<DataFragment<SendQueueItem>> GetSendQueueItemsDataFragmentAsync(SendQueueFilter filter,
        GridDataProviderRequest<SendQueueItem> request, CancellationToken cancellationToken = default);
    
    Task<DataFragment<Partner>> GetPartnersDataFragmentAsync(PartnerFilter filter, 
        GridDataProviderRequest<Partner> request, CancellationToken cancellationToken = default);
    
    Task<DataFragment<Identity>> GetIdentitiesDataFragmentAsync(IdentityFilter filter, 
        GridDataProviderRequest<Identity> request, CancellationToken cancellationToken = default);
    
    Task<DataFragment<Listener>> GetListenersDataFragmentAsync(ListenerFilter filter, GridDataProviderRequest<Listener> request, 
        CancellationToken cancellationToken = default);

    Task DeletePartnerAsync(Partner partner);
    Task SavePartnerAsync(Partner currentPartner);
    Task<DataFragment<Certificate>> GetCertificatesDataFragmentAsync(CertificateFilter filterModel, GridDataProviderRequest<Certificate> request, CancellationToken requestCancellationToken);
    Task DeleteCertificateAsync(Certificate certificate);
    Task SaveCertificateAsync(Certificate currentCertificate);
    Task DeleteIdentityAsync(Identity identity);
    Task SaveIdentityAsync(Identity identity);
    Task DeleteSendQueueItemAsync(SendQueueItem sendQueueItem);
    Task SaveSendQueueItemAsync(SendQueueItem sendQueueItem);
    Task<List<Partner>> GetAllPartnersAsync();
    Task<List<Identity>> GetAllIdentitiesAsync();
    Task<List<SendQueueItem>> GetAllSendQueueItemsAsync();
    Task<List<SendQueueItem>> GetSendQueueItemsToProcessAsync();
    Task DeleteAllSendQueueItemsAsync();

    /// <summary>Puts a failed or finished item back to the queue.</summary>
    Task RequeueSendQueueItemAsync(SendQueueItem sendQueueItem);

    Task SaveListenerAsync(Listener listener);
    Task DeleteListenerAsync(Listener listener);
    Task<List<Certificate>> GetAllCertificatesAsync();

    Task<DataFragment<ReceivedFile>> GetReceivedFilesDataFragmentAsync(ReceivedFileFilter filter,
        GridDataProviderRequest<ReceivedFile> request, CancellationToken cancellationToken = default);
    Task<ReceivedFile?> GetReceivedFileAsync(int id);
    Task MarkReceivedFileFetchedAsync(ReceivedFile receivedFile);
}