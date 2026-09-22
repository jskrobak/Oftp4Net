using Havit.Blazor.Components.Web.Bootstrap;
using Havit.Data.Patterns.UnitOfWorks;
using Oftp4Net.DataLayer;
using Oftp4Net.DataLayer.Filters;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;

namespace Oftp4Net.Services;

public class DataService(
    IPartnerRepository partnerRepository,
    ICertificateRepository certificateRepository,
    IIdentityRepository identityRepository,
    ISendQueueItemRepository sendQueueItemRepository,
    IListenerRepository listenerRepository,
    IReceivedFileRepository receivedFileRepository,
    IUnitOfWork unitOfWork): IDataService
{
    public async Task<DataFragment<SendQueueItem>> GetSendQueueItemsDataFragmentAsync(SendQueueFilter filter, GridDataProviderRequest<SendQueueItem> request,
        CancellationToken cancellationToken = default)
    {
        return await sendQueueItemRepository.GetFragmentAsync(filter, request, cancellationToken);
    }

    public async Task<DataFragment<Partner>> GetPartnersDataFragmentAsync(PartnerFilter filter, GridDataProviderRequest<Partner> request,
        CancellationToken cancellationToken = default)
    {
        return await partnerRepository.GetFragmentAsync(filter, request, cancellationToken);
    }
    
    public async Task<DataFragment<Listener>> GetListenersDataFragmentAsync(ListenerFilter filter, GridDataProviderRequest<Listener> request, CancellationToken cancellationToken = default)
    {
        return await listenerRepository.GetFragmentAsync(filter, request, cancellationToken);
    }

    
    public async Task DeletePartnerAsync(Partner partner)
    {
        unitOfWork.AddForDelete(partner);
        await unitOfWork.CommitAsync();
    }

    public async Task SavePartnerAsync(Partner currentPartner)
    {
        if(currentPartner.Id == 0)
            unitOfWork.AddForInsert(currentPartner);
        else
            unitOfWork.AddForUpdate(currentPartner);
        
        await unitOfWork.CommitAsync();
    }

    public async Task<DataFragment<Certificate>> GetCertificatesDataFragmentAsync(CertificateFilter filterModel, GridDataProviderRequest<Certificate> request,
        CancellationToken requestCancellationToken)
    {
        return await certificateRepository.GetFragmentAsync(filterModel, request, requestCancellationToken);
    }

    public async Task DeleteCertificateAsync(Certificate certificate)
    {
        unitOfWork.AddForDelete(certificate);
        await unitOfWork.CommitAsync();
    }

    public async Task SaveCertificateAsync(Certificate currentCertificate)
    {
        if(currentCertificate.Id == 0)
            unitOfWork.AddForInsert(currentCertificate);
        else
            unitOfWork.AddForUpdate(currentCertificate);
        
        await unitOfWork.CommitAsync();
    }

    public async Task<DataFragment<Identity>> GetIdentitiesDataFragmentAsync(IdentityFilter filterModel, GridDataProviderRequest<Identity> request,
        CancellationToken requestCancellationToken)
    {
        return await identityRepository.GetFragmentAsync(filterModel, request, requestCancellationToken);
    }

    public async Task DeleteIdentityAsync(Identity identity)
    {
        unitOfWork.AddForDelete(identity);
        await unitOfWork.CommitAsync();
    }

    public async Task SaveIdentityAsync(Identity identity)
    {
        if(identity.Id == 0)
            unitOfWork.AddForInsert(identity);
        else
            unitOfWork.AddForUpdate(identity);
        
        await unitOfWork.CommitAsync();
    }

    public async Task DeleteSendQueueItemAsync(SendQueueItem sendQueueItem)
    {
        unitOfWork.AddForDelete(sendQueueItem);
        await unitOfWork.CommitAsync();
    }

    public async Task SaveSendQueueItemAsync(SendQueueItem sendQueueItem)
    {
        if(sendQueueItem.Id == 0)
            unitOfWork.AddForInsert(sendQueueItem);
        else
            unitOfWork.AddForUpdate(sendQueueItem);
        
        await unitOfWork.CommitAsync();
    }

    public async Task<List<Partner>> GetAllPartnersAsync()
    {
        return await partnerRepository.GetAllAsync();
    }

    public async Task<List<Identity>> GetAllIdentitiesAsync()
    {
        return await identityRepository.GetAllAsync();
    }

    public async Task<List<SendQueueItem>> GetAllSendQueueItemsAsync()
    {
        return await sendQueueItemRepository.GetAllWithRefsAsync();
    }

    public async Task<List<SendQueueItem>> GetSendQueueItemsToProcessAsync()
    {
        return await sendQueueItemRepository.GetAllToProcessAsync();
    }

    public async Task DeleteAllSendQueueItemsAsync()
    {
        unitOfWork.AddRangeForDelete(await sendQueueItemRepository.GetAllAsync());
        await unitOfWork.CommitAsync();
    }

    public async Task RequeueSendQueueItemAsync(SendQueueItem sendQueueItem)
    {
        sendQueueItem.Status = SendStatus.NEW;
        sendQueueItem.RetryCount = 0;
        sendQueueItem.NextRetry = DateTime.MinValue;
        sendQueueItem.LastError = null;
        sendQueueItem.LastErrorDate = null;
        sendQueueItem.SentDate = null;
        sendQueueItem.DeliveredDate = null;
        unitOfWork.AddForUpdate(sendQueueItem);
        await unitOfWork.CommitAsync();
    }

    public async Task SaveListenerAsync(Listener listener)
    {
        if (listener.Id == 0)
            unitOfWork.AddForInsert(listener);
        else
            unitOfWork.AddForUpdate(listener);

        await unitOfWork.CommitAsync();
    }

    public async Task DeleteListenerAsync(Listener listener)
    {
        unitOfWork.AddForDelete(listener);
        await unitOfWork.CommitAsync();
    }

    public async Task<List<Certificate>> GetAllCertificatesAsync()
    {
        return await certificateRepository.GetAllAsync();
    }

    public async Task<DataFragment<ReceivedFile>> GetReceivedFilesDataFragmentAsync(ReceivedFileFilter filter,
        GridDataProviderRequest<ReceivedFile> request, CancellationToken cancellationToken = default)
    {
        return await receivedFileRepository.GetFragmentAsync(filter, request, cancellationToken);
    }

    public async Task<ReceivedFile?> GetReceivedFileAsync(int id)
    {
        try
        {
            return await receivedFileRepository.GetObjectAsync(id);
        }
        catch (Havit.Data.Patterns.Exceptions.ObjectNotFoundException)
        {
            return null;
        }
    }
}
